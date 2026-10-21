namespace AgentPace.Core;

public static class ClaudeBridge
{
    public static bool IsClaude(string? raw) => Uri.TryCreate(raw, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host == "claude.ai" && u.IsDefaultPort && u.UserInfo == "";
    public const string Script = """

    if (location.origin !== 'https://claude.ai') return JSON.stringify({status:401});
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 20000);
    const get = async path => {
        const r = await fetch(path, {method:'GET', credentials:'same-origin',
            cache:'no-store', redirect:'error', headers:{Accept:'application/json'}, signal:controller.signal});
        if (!r.ok) return {status:r.status, retryAfter:Number(r.headers.get('Retry-After')) || 600};
        if (!(r.headers.get('Content-Type') || '').includes('application/json')) return {status:401};
        const text = await r.text();
        if (text.length > 1000000) return {status:502};
        return {status:200, data:JSON.parse(text)};
    };
    try {
        const result = await get('/api/organizations');
        if (result.status !== 200) return JSON.stringify(result);
        if (!Array.isArray(result.data)) return JSON.stringify({status:502});
        const organizations = result.data.filter(o => typeof o.uuid === 'string' &&
            /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(o.uuid))
            .map(o => ({uuid:o.uuid, name:typeof o.name === 'string' ? o.name.slice(0,120) : 'Claude'}));
        const selected = organizations.find(o => o.uuid === selectedOrganization);
        // Never silently switch to a different organization when a saved choice disappears.
        const org = selected || (!selectedOrganization && organizations.length === 1 ? organizations[0] : null);
        if (!org) return JSON.stringify({status:organizations.length ? 409 : 401, organizations});
        const resultUsage = await get('/api/organizations/' + encodeURIComponent(org.uuid) + '/usage');
        if (resultUsage.status !== 200) return JSON.stringify({status:resultUsage.status, retryAfter:resultUsage.retryAfter, organizations});
        const usage = {};
        for (const key of ['five_hour','seven_day','seven_day_sonnet','seven_day_opus']) {
            const window = resultUsage.data?.[key];
            if (window && typeof window.utilization === 'number' && Number.isFinite(window.utilization)) {
                usage[key] = {utilization:window.utilization, resets_at:typeof window.resets_at === 'string' ? window.resets_at : null};
            }
        }
        // Current Claude usage identifies Fable through a model-scoped weekly
        // limit. Internal bucket codenames are not a stable model identifier.
        const fable = Array.isArray(resultUsage.data?.limits) ? resultUsage.data.limits.filter(row =>
            row?.kind === 'weekly_scoped' && row?.scope?.model?.display_name === 'Fable') : [];
        if (fable.length === 1 && typeof fable[0].percent === 'number' &&
            Number.isFinite(fable[0].percent) && fable[0].percent >= 0) {
            usage.seven_day_fable = {utilization:fable[0].percent,
                resets_at:typeof fable[0].resets_at === 'string' ? fable[0].resets_at : null};
        }
        return JSON.stringify({status:200, organizations, organization:org.uuid, usage});
    } catch { return JSON.stringify({status:503}); }
    finally { clearTimeout(timer); }

""";
}
