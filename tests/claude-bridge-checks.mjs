import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';

// Execute the shipped bridge against synthetic GET responses, never an account.
const source = readFileSync(new URL('../src/AgentPace.Core/ClaudeBridge.cs', import.meta.url), 'utf8');
const script = source.split('public const string Script = """')[1].split('""";')[0];
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
const bridge = new AsyncFunction('selectedOrganization', script);
const org = '00000000-0000-0000-0000-000000000001';
globalThis.location = {origin: 'https://claude.ai'};
const legacy = {utilization: 90, resets_at: '2026-11-01T18:00:00Z'};
const weekly = {utilization: 30, resets_at: '2026-11-08T18:00:00Z'};
const row = (kind, percent = 0, is_active = true, model) => ({kind, percent, is_active,
    resets_at: '2026-11-08T18:00:00Z', scope: model ? {model: {display_name: model}} : null});
const cases = [
    ['legacy missing flags remains compatible', {five_hour: legacy, seven_day: weekly}, ['five_hour', 'seven_day']],
    ['modern inactive session overrides live legacy', {five_hour: legacy, limits: [row('session', 2, false), row('weekly_all', 3), row('weekly_scoped', 2, false, 'Fable')]}, ['seven_day'], false, {seven_day: 3}],
    ['active zero is retained', {five_hour: legacy, limits: [row('session', 0)]}, ['five_hour'], false, {five_hour: 0}],
    ['modern only models and windows', {limits: [row('session', 1), row('weekly_all', 2), row('weekly_scoped', 3, true, 'Fable'), row('weekly_scoped', 4, true, 'Sonnet'), row('weekly_scoped', 5, true, 'Opus')]}, ['five_hour', 'seven_day', 'seven_day_fable', 'seven_day_sonnet', 'seven_day_opus']],
    ['missing modern type uses only its legacy fallback', {five_hour: legacy, limits: [row('weekly_all', 2)]}, ['five_hour', 'seven_day']],
    ['legacy inactive is excluded', {five_hour: {...legacy, is_active: false}, seven_day: weekly}, ['seven_day']],
    ['modern missing flag stays compatible', {limits: [{kind: 'session', percent: 0}]}, ['five_hour']],
    ['unobserved legacy Fable alias is not inferred', {seven_day_fable: weekly}, []],
    ['duplicate type never chooses or revives legacy', {five_hour: legacy, seven_day: weekly, limits: [row('session'), row('session', 4, false)]}, ['seven_day']],
    ['invalid matching percent never revives legacy', {five_hour: legacy, seven_day: weekly, limits: [row('session', '2')]}, ['seven_day']],
    ['confirmed all inactive produces explicit empty', {limits: [row('session', 2, false), row('weekly_all', 3, false)]}, [], true],
    ['unknown inactive alone cannot clear cache', {limits: [row('unknown', 0, false)]}, []],
    ['recognized and unknown both inactive can clear cache', {limits: [row('session', 2, false), row('unknown', 0, false)]}, [], true],
    ['unknown active prevents empty confirmation', {limits: [row('session', 2, false), row('unknown')]}, []],
    ['malformed row prevents empty confirmation', {limits: [row('session', 2, false), null]}, []],
    ['legacy active fallback prevents empty confirmation', {seven_day: weekly, limits: [row('session', 2, false)]}, ['seven_day']],
    ['empty array does not confirm inactivity', {limits: []}, []],
    ['unknown payload does not confirm inactivity', {}, []],
];
for (const flag of [null, 0, 1, 'false', 'true']) {
    cases.push([`bad modern flag ${JSON.stringify(flag)} never revives legacy`, {five_hour: legacy, seven_day: weekly, limits: [row('session', 2, flag)]}, ['seven_day']]);
    cases.push([`bad legacy flag ${JSON.stringify(flag)} is excluded`, {five_hour: {...legacy, is_active: flag}, seven_day: weekly}, ['seven_day']]);
}
for (const [name, payload, keys, noActive = false, percents = {}] of cases) {
    const calls = [];
    globalThis.fetch = async (path, options) => {
        calls.push([path, options.method]);
        return {ok: true, status: 200, headers: {get: () => 'application/json'}, text: async () =>
            JSON.stringify(path === '/api/organizations' ? [{uuid: org, name: 'Synthetic account'}] : {...payload, secret: 'must not cross'})};
    };
    const normalized = JSON.parse(await bridge(''));
    assert.equal(normalized.status, 200, name);
    assert.deepEqual(Object.keys(normalized.usage).sort(), [...keys].sort(), name);
    assert.equal(normalized.noActiveLimits, noActive, name);
    for (const [key, percent] of Object.entries(percents)) assert.equal(normalized.usage[key].utilization, percent, name);
    assert.equal(JSON.stringify(normalized).includes('must not cross'), false, name);
    assert.equal(calls.every(([, method]) => method === 'GET'), true, name);
    console.log('PASS ' + name);
}
console.log(`${cases.length}/${cases.length} Claude bridge checks passed`);
