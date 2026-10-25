const { test } = require('node:test');
const assert = require('node:assert/strict');
let policy = {};
try { policy = require('./artifact-maintenance.cjs'); } catch (e) { if (e.code !== 'MODULE_NOT_FOUND') throw e; }
const now = Date.parse('2026-10-25T14:00:00Z');
function a(id,name,extra={}) { return {id,name,size_in_bytes:100,created_at:'2026-10-20T00:00:00Z',expired:false,workflow_run:{id,head_branch:'main'},runStatus:'completed',conclusion:'success',...extra}; }
function plan(items,extra={}) { return policy.planCleanup(items,{now,defaultBranch:'main',repository:'pinyin-gloss',...extra}); }
test('只删除已完成且超过一天的已知历史产物',()=>{
 const p=plan([a(1,'core-test-results'),a(2,'unknown-data'),a(3,'core-test-results',{created_at:'2026-10-25T13:00:00Z'}),a(4,'core-test-results',{runStatus:'in_progress'})]);
 assert.deepEqual(p.remove.map(x=>x.id),[1]);
});
test('临时包保留最新成功默认分支版本，失败或分支构建不能替代',()=>{
 const p=plan([a(1,'pinyin-gloss-win-x64'),a(2,'pinyin-gloss-win-x64',{created_at:'2026-10-21T00:00:00Z'}),a(3,'pinyin-gloss-win-x64',{created_at:'2026-10-22T00:00:00Z',conclusion:'failure'}),a(4,'pinyin-gloss-win-x64',{created_at:'2026-10-23T00:00:00Z',workflow_run:{id:4,head_branch:'feat/x'}})]);
 assert.deepEqual(p.keep.map(x=>x.id),[2]);
 assert.deepEqual(p.remove.map(x=>x.id),[1,3,4]);
});
test('显式行情和事件来源优先于最新排序',()=>{
 const items=[a(1,'stocktrend-market-data-1'),a(2,'stocktrend-market-data-2'),a(3,'stocktrend-market-data-3'),a(4,'daily-stock-report-8',{workflow_run:{id:8,head_branch:'main'}}),a(5,'daily-stock-report-9'),a(6,'daily-stock-report-10')];
 const p=plan(items,{repository:'stock-trend',pointer:{artifactId:1,artifactName:'stocktrend-market-data-1',producerRunId:'1',eventProducerRunId:'8'}});
 assert.ok(p.keep.some(x=>x.id===1)); assert.ok(p.keep.some(x=>x.id===4));
});
test('无指针或指针不匹配时股票清理必须失败关闭',()=>{
 assert.throws(()=>plan([a(1,'stocktrend-market-data-1')],{repository:'stock-trend'}),/pointer/);
 assert.throws(()=>plan([a(1,'stocktrend-market-data-1')],{repository:'stock-trend',pointer:{artifactId:1,artifactName:'other',producerRunId:'1'}}),/pointer/);
});
test('保留两份成功快照并清理失败的旧快照',()=>{
 const items=[1,2,3,4].map(id=>a(id,'site-generation-'+id,{created_at:'2026-10-2'+id+'T00:00:00Z',conclusion:id===4?'failure':'success'}));
 items.push(a(99,'stocktrend-market-data-99'));
 const p=plan(items,{repository:'stock-trend',pointer:{artifactId:99,artifactName:'stocktrend-market-data-99',producerRunId:'99'}});
 assert.deepEqual(p.keep.filter(x=>x.id!==99).map(x=>x.id),[2,3]); assert.deepEqual(p.remove.map(x=>x.id),[1,4]);
});
test('邮件去重标记和未知文件不提前清理',()=>{
 assert.deepEqual(plan([a(1,'stocktrend-email-2026-10-20'),a(2,'release-package')]).remove,[]);
});
test('其他任务运行时不删除',()=>{
 assert.deepEqual(plan([a(1,'core-test-results')],{busy:true}).remove,[]);
});
test('过期文件不再列入占用与删除',()=>{
 const p=plan([a(1,'core-test-results',{expired:true})]); assert.equal(p.bytesBefore,0); assert.deepEqual(p.remove,[]);
});
test('审计模式不调用删除接口',async()=>{
 let removed=0;
 const result=await policy.executePlan({remove:[a(1,'core-test-results')],bytesBefore:100},{mode:'audit',deleteArtifact:async()=>removed++});
 assert.equal(removed,0); assert.equal(result.deletedBytes,0);
});
test('清理模式逐个删除且容忍已被其他任务删掉的 404',async()=>{
 const ids=[]; const result=await policy.executePlan({remove:[a(1,'core-test-results'),a(2,'core-test-results')],bytesBefore:200},{mode:'cleanup',deleteArtifact:async id=>{ids.push(id); if(id===2)throw Object.assign(new Error('gone'),{status:404});}});
 assert.deepEqual(ids,[1,2]); assert.equal(result.deletedBytes,200); assert.equal(result.deletedCount,2);
});
test('清理权限错误不得伪装成功',async()=>{
 await assert.rejects(()=>policy.executePlan({remove:[a(1,'core-test-results')],bytesBefore:100},{mode:'cleanup',deleteArtifact:async()=>{throw Object.assign(new Error('forbidden'),{status:403});}}),/forbidden/);
});
function client(artifacts, {busy=false, pointer=null}={}) {
 const deleted=[];
 const api={
  rest:{repos:{get:async()=>({data:{default_branch:'main'}}),getContent:async()=>({data:{content:Buffer.from(JSON.stringify(pointer)).toString('base64')}})},
    actions:{listArtifactsForRepo:'artifacts',listWorkflowRunsForRepo:'runs',getWorkflowRun:async({run_id})=>({data:{status:'completed',conclusion:'success',path:'.github/workflows/ci.yml'}}),deleteArtifact:async({artifact_id})=>{deleted.push(artifact_id);artifacts=artifacts.filter(a=>a.id!==artifact_id);}}},
  paginate:async(method,args)=>method==='artifacts'?artifacts:(busy&&args.status==='queued'?[{id:999}]:[])
 };
 const core={info:()=>{},summary:{addHeading(){return this},addTable(){return this},addRaw(){return this},async write(){}}};
 return {github:api,context:{repo:{owner:'test',repo:pointer?'stock-trend':'pinyin-gloss'},ref:'refs/heads/main',runId:123},core,deleted};
}
test('完整流程重新读取并验证删除后的占用',async()=>{
 const c=client([a(1,'core-test-results')]);const r=await policy.runMaintenance({...c,mode:'cleanup'});
 assert.deepEqual(c.deleted,[1]);assert.equal(r.bytesAfter,0);assert.equal(r.countAfter,0);
});
test('完整流程发现排队消费者后只审计',async()=>{
 const c=client([a(1,'core-test-results')],{busy:true});const r=await policy.runMaintenance({...c,mode:'cleanup'});
 assert.deepEqual(c.deleted,[]);assert.equal(r.busy,true);assert.equal(r.bytesAfter,100);
});
test('错误分支不能执行清理',async()=>{
 const c=client([a(1,'core-test-results')]);c.context.ref='refs/heads/feature';
 await assert.rejects(()=>policy.runMaintenance({...c,mode:'cleanup'}),/default branch/);assert.deepEqual(c.deleted,[]);
});
