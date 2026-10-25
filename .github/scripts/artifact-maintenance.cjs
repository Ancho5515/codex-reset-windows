'use strict';

const DAY = 86400000;
const family = name => {
  if (name === 'codex-reset-windows-x64' || name === 'pinyin-gloss-win-x64') return 'package';
  if (/^site-generation-/.test(name)) return 'snapshot';
  if (/^stocktrend-market-data-/.test(name)) return 'market';
  if (/^daily-stock-report-/.test(name)) return 'report';
  if (/^(core-test-results|admin-(preview|runtime|candidate|prepared|publish|result)-|site-(input|refresh|result)-)/.test(name)) return 'temporary';
  return null;
};

function planCleanup(artifacts, options = {}) {
  const { now = Date.now(), defaultBranch, repository, pointer, busy = false } = options;
  const items = artifacts.filter(a => !a.expired);
  const protectedIds = new Set();
  let canonicalPackage;
  if (repository === 'stock-trend') {
    const referenced = items.find(a => a.id === Number(pointer?.artifactId));
    if (!referenced || referenced.name !== pointer?.artifactName ||
        String(referenced.workflow_run?.id) !== String(pointer?.producerRunId)) {
      throw new Error('Cannot validate current market-data pointer; refusing cleanup.');
    }
    protectedIds.add(referenced.id);
    const eventRun = String(pointer.eventProducerRunId || pointer.producerRunId);
    for (const a of items) {
      if (a.name === 'daily-stock-report-' + eventRun) protectedIds.add(a.id);
    }
  }
  for (const group of ['package', 'snapshot', 'market', 'report']) {
    const newest = items.filter(a => family(a.name) === group &&
      a.conclusion === 'success' && a.workflow_run?.head_branch === defaultBranch)
      .sort((a,b) => Date.parse(b.created_at) - Date.parse(a.created_at) || b.id - a.id);
    for (const a of newest.slice(0, group === 'package' ? 1 : 2)) protectedIds.add(a.id);
    if (group === 'package') canonicalPackage = newest[0];
  }
  const keep = [], remove = [];
  for (const a of items) {
    const age = now - Date.parse(a.created_at);
    const sha = canonicalPackage?.workflow_run?.head_sha;
    const redundantPackage = family(a.name) === 'package' && /^[0-9a-f]{40}$/.test(sha || '') &&
      a.workflow_run?.head_sha === sha && a.workflow_run?.head_branch !== defaultBranch &&
      a.name === canonicalPackage.name && a.conclusion === 'success';
    const eligible = !busy && Number.isFinite(age) && (age >= DAY || redundantPackage) &&
      a.runStatus === 'completed' && family(a.name) && !protectedIds.has(a.id);
    (eligible ? remove : keep).push(a);
  }
  return { keep, remove, busy, bytesBefore: items.reduce((n,a) => n + a.size_in_bytes, 0) };
}

async function executePlan(plan, { mode, deleteArtifact }) {
  if (!['audit', 'cleanup'].includes(mode)) throw new Error('Invalid maintenance mode');
  let deletedBytes = 0, deletedCount = 0;
  if (mode === 'cleanup') {
    for (const a of plan.remove) {
      try { await deleteArtifact(a.id); }
      catch (e) { if (e.status !== 404) throw e; }
      deletedBytes += a.size_in_bytes;
      deletedCount++;
    }
  }
  return { deletedBytes, deletedCount };
}

async function runMaintenance({ github, context, core, mode = 'audit' }) {
  const { owner, repo } = context.repo;
  const params = { owner, repo };
  const metadata = (await github.rest.repos.get(params)).data;
  const defaultBranch = metadata.default_branch;
  if (context.ref !== 'refs/heads/' + defaultBranch) throw new Error('Maintenance must use the default branch.');
  const artifacts = await github.paginate(github.rest.actions.listArtifactsForRepo, { ...params, per_page: 100 });
  const runs = new Map();
  for (const a of artifacts.filter(a => !a.expired)) {
    const id = a.workflow_run?.id;
    if (!id) continue;
    if (!runs.has(id)) runs.set(id, (await github.rest.actions.getWorkflowRun({ ...params, run_id: id })).data);
    const run = runs.get(id);
    a.runStatus = run.status;
    a.conclusion = run.conclusion;
    a.workflowPath = run.path;
  }
  // Do not prune while another producer or consumer is active.
  const activeRuns = async () => (await github.paginate(
    github.rest.actions.listWorkflowRunsForRepo, { ...params, per_page: 100 }
  )).filter(r => String(r.id) !== String(context.runId) && r.status !== 'completed');
  let busy = (await activeRuns()).length > 0;
  let pointer;
  if (repo === 'stock-trend') {
    const response = await github.rest.repos.getContent({ ...params, path: 'data/market-data-snapshot.json', ref: defaultBranch });
    pointer = JSON.parse(Buffer.from(response.data.content, 'base64').toString('utf8'));
  }
  let plan = planCleanup(artifacts, { defaultBranch, repository: repo, pointer, busy });
  if (mode === 'cleanup' && plan.remove.length && !busy) {
    busy = (await activeRuns()).length > 0;
    if (busy) plan = planCleanup(artifacts, { defaultBranch, repository: repo, pointer, busy });
  }
  const describe = a => ({ id:a.id, name:a.name, bytes:a.size_in_bytes, workflow:a.workflowPath, run:a.workflow_run?.id });
  core.info('ARTIFACT_AUDIT ' + JSON.stringify({ bytesBefore:plan.bytesBefore, busy:plan.busy, keep:plan.keep.map(describe), candidates:plan.remove.map(describe) }));
  const result = await executePlan(plan, {
    mode,
    deleteArtifact: artifact_id => github.rest.actions.deleteArtifact({ ...params, artifact_id })
  });
  // Verify the API state, rather than treating accepted DELETE requests as completion.
  const after = await github.paginate(github.rest.actions.listArtifactsForRepo, { ...params, per_page: 100 });
  const liveAfter = after.filter(a => !a.expired);
  const bytesAfter = liveAfter.reduce((n,a) => n + a.size_in_bytes, 0);
  if (mode === 'cleanup' && plan.remove.some(a => liveAfter.some(b => b.id === a.id))) {
    throw new Error('Some selected artifacts remain after cleanup.');
  }
  core.info('ARTIFACT_RESULT ' + JSON.stringify({ ...result, bytesBefore:plan.bytesBefore, bytesAfter, countAfter:liveAfter.length, busy:plan.busy }));
  await core.summary.addHeading('Actions Artifact 审计与清理')
    .addTable([
      [{data:'指标',header:true},{data:'值',header:true}],
      ['模式',mode],['清理前字节',String(plan.bytesBefore)],['删除数量',String(result.deletedCount)],
      ['释放字节',String(result.deletedBytes)],['清理后字节',String(bytesAfter)],
      ['保留数量',String(liveAfter.length)],['其他任务运行中',String(plan.busy)]
    ]).addRaw('\n完整逐项清单见 ARTIFACT_AUDIT 日志。Release assets 不在本流程操作范围。\n').write();
  return { ...result, bytesBefore:plan.bytesBefore, bytesAfter, countAfter:liveAfter.length, busy:plan.busy };
}

module.exports = { planCleanup, executePlan, runMaintenance };

