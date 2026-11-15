const assert = require('node:assert/strict');
const scripts = JSON.parse(require('node:fs').readFileSync(0, 'utf8'));
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
let checks = 0;
const goodRelease = () => ({id: 17, tag_name: 'v1.0.0', published_at: new Date(Date.now() - 49 * 3600000).toISOString(), draft: false, prerelease: true, body: 'Release notes', assets: [{name: 'EricWorkHub_1.0.0.zip', size: 4096}]});
function environment(options = {}) {
    const original = goodRelease();
    const current = {...original, ...options.current};
    const calls = {updates: [], uploads: [], deleted: [], get: 0};
    const data = value => Promise.resolve({data: value});
    const repos = {
        getRelease: async () => {
            calls.get++;
            if (options.removed) throw Object.assign(new Error('removed'), {status: 404});
            if (options.changeAfter && calls.get >= options.changeAfter) current.published_at = '2099-01-01T00:00:00Z';
            return {data: current};
        },
        updateRelease: async request => { calls.updates.push(request); return {data: {...current, ...request}}; },
        listReleaseAssets: async () => ({data: [{id: 19, name: 'EricWorkHub_1.0.0.zip'}, {id: 20, name: 'unrelated.txt'}]}),
        deleteReleaseAsset: async request => { calls.deleted.push(request); },
        uploadReleaseAsset: async request => { calls.uploads.push(request); },
        listReleases: async () => ({data: options.empty ? [] : [current]})
    };
    const issues = {listForRepo: async () => ({data: options.blocked ? [{number: 1}] : []})};
    const runs = options.runs ?? [{event: 'release', head_sha: 'commit', run_number: 1, run_attempt: 1, id: 10, status: 'completed', conclusion: 'success'}];
    const github = {
        rest: {repos, issues, git: {
            getRef: async () => ({data: {object: {sha: options.annotated ? 'tag-sha' : 'commit', type: options.annotated ? 'tag' : 'commit'}}}),
            getTag: async () => ({data: {object: {sha: 'commit', type: 'commit'}}})
        }, actions: {listWorkflowRuns: async () => ({data: {workflow_runs: runs}})}},
        paginate: async (method, request) => (await method(request)).data
    };
    const context = {repo: {owner: 'owner', repo: 'EricWorkHub'}, payload: {release: original}};
    const fakeRequire = name => name === 'fs' ? {readFileSync: file => file === '.releasenote.md' ? (options.emptyNotes ? '' : 'Tagged final notes') : Buffer.alloc(options.emptyPackage ? 0 : 100)} : require(name);
    const execute = job => new AsyncFunction('github', 'context', 'core', 'require', 'process', 'console', scripts[job])(
        github, context, {info() {}}, fakeRequire, {env: {PACKAGE_PATH: 'EricWorkHub_1.0.0.zip'}}, {log() {}});
    return {execute, calls, current};
}
async function scenario(name, test) { await test(); checks++; }
(async () => {
    await scenario('prepare matching release', async () => {
        const e = environment(); await e.execute('prepare_release'); assert.equal(e.calls.updates.length, 1); assert.match(e.calls.updates[0].body, /正在构建/);
    });
    for (const current of [{id: 99}, {tag_name: 'v9.0.0'}, {published_at: '2099-01-01'}, {draft: true}]) {
        await scenario('stale prepare rejected', async () => { const e = environment({current}); await assert.rejects(e.execute('prepare_release')); assert.equal(e.calls.updates.length, 0); });
    }
    await scenario('upload only matching package', async () => {
        const e = environment(); await e.execute('upload_asset'); assert.deepEqual(e.calls.deleted.map(x => x.asset_id), [19]); assert.equal(e.calls.uploads[0].name, 'EricWorkHub_1.0.0.zip'); assert.equal(e.calls.uploads.length, 1);
    });
    await scenario('empty package rejected before deleting assets', async () => {
        const e = environment({emptyPackage: true}); await assert.rejects(e.execute('upload_asset')); assert.equal(e.calls.deleted.length, 0);
    });
    await scenario('changed release prevents replacement upload', async () => {
        const e = environment({changeAfter: 3}); await assert.rejects(e.execute('upload_asset')); assert.equal(e.calls.uploads.length, 0);
    });
    await scenario('tagged release notes published', async () => {
        const e = environment(); await e.execute('publish_notes'); assert.equal(e.calls.updates[0].body, 'Tagged final notes');
    });
    await scenario('empty notes rejected', async () => { const e = environment({emptyNotes: true}); await assert.rejects(e.execute('publish_notes')); assert.equal(e.calls.updates.length, 0); });
    await scenario('failed release returned to draft', async () => {
        const e = environment(); await e.execute('draft_failed_release'); assert.equal(e.calls.updates[0].draft, true); assert.equal(e.calls.updates[0].body, undefined);
    });
    for (const options of [{current: {draft: true}}, {current: {published_at: '2099-01-01'}}, {removed: true}]) {
        await scenario('old failure leaves new release alone', async () => { const e = environment(options); await e.execute('draft_failed_release'); assert.equal(e.calls.updates.length, 0); });
    }
    await scenario('eligible prerelease promoted', async () => {
        const e = environment(); await e.execute('promote'); assert.equal(e.calls.updates[0].make_latest, 'true'); assert.equal(e.calls.updates[0].prerelease, false);
    });
    await scenario('annotated release tag supported', async () => { const e = environment({annotated: true}); await e.execute('promote'); assert.equal(e.calls.updates.length, 1); });
    for (const options of [
        {empty: true}, {current: {prerelease: false}}, {current: {body: '[no-release]'}},
        {current: {published_at: new Date().toISOString()}}, {current: {assets: []}},
        {current: {assets: [{name: 'EricWorkHub_1.0.0.zip', size: 0}]}}, {runs: []}, {blocked: true},
        {runs: [{event: 'release', head_sha: 'other', status: 'completed', conclusion: 'success'}]},
        {runs: [{event: 'workflow_dispatch', head_sha: 'commit', status: 'completed', conclusion: 'success'}]},
        {runs: [{event: 'release', head_sha: 'commit', run_number: 2, run_attempt: 1, id: 2, status: 'completed', conclusion: 'failure'},
            {event: 'release', head_sha: 'commit', run_number: 1, run_attempt: 1, id: 1, status: 'completed', conclusion: 'success'}]},
        {runs: [{event: 'release', head_sha: 'commit', run_number: 2, run_attempt: 2, id: 2, status: 'in_progress', conclusion: null},
            {event: 'release', head_sha: 'commit', run_number: 2, run_attempt: 1, id: 2, status: 'completed', conclusion: 'success'}]}
    ]) {
        await scenario('ineligible prerelease blocked', async () => { const e = environment(options); await e.execute('promote'); assert.equal(e.calls.updates.length, 0); });
    }
    process.stdout.write(`PASS ${checks} isolated scenarios execute actual workflow JavaScript\n`);
})().catch(error => { console.error(error); process.exitCode = 1; });
