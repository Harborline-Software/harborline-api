import {test} from 'node:test'
import assert from 'node:assert/strict'
import {route} from './workload-route.mjs'
const policy = {routes: {'legacy-windows': {qualified: true, labels: ['self-hosted','Windows','winbox']}, mac16: {qualified: false, labels: ['self-hosted','macOS','X64','mac16']}}}
const main = {GITHUB_EVENT_NAME:'schedule', GITHUB_REF:'refs/heads/main'}
test('legacy default keeps the existing route', () => assert.deepEqual(route(policy,main).labels, policy.routes['legacy-windows'].labels))
test('unqualified and unknown routes never dispatch local work', () => {for (const name of ['mac16','mini','unknown']) assert.equal(route(policy,{...main,BACKGROUND_MUTATION_ROUTE:name}).enabled,false)})
test('pause and reservation explain refusal', () => {assert.match(route(policy,{...main,BACKGROUND_MUTATION_ROUTE:'paused'}).reason,/paused/); assert.equal(route(policy,{...main,LOCAL_BACKGROUND_RESERVATION:'perf'}).enabled,false)})
test('non-main dispatch cannot reach a persistent runner', () => assert.equal(route(policy,{...main,GITHUB_REF:'refs/heads/feature'}).enabled,false))
test('PR feedback remains hosted despite local reservation', () => assert.equal(route(policy,{...main,GITHUB_EVENT_NAME:'pull_request',LOCAL_BACKGROUND_RESERVATION:'perf'}).labels,'ubuntu-latest'))
test('committed qualification enables exact labels', () => assert.deepEqual(route({...policy,routes:{...policy.routes,mac16:{...policy.routes.mac16,qualified:true,evidence:"T-679 same-tree qualification"}}},{...main,BACKGROUND_MUTATION_ROUTE:'mac16'}).labels,policy.routes.mac16.labels))

test('qualification flag without evidence remains disabled', () => assert.equal(route({routes:{mac16:{qualified:true,labels:[]}}},{...main,BACKGROUND_MUTATION_ROUTE:'mac16'}).enabled,false))

test('qualification requires a boolean true', () => { for (const qualified of ['false', 'true', 1]) assert.equal(route({routes:{mac16:{qualified,evidence:'recorded',labels:[]}}},{...main,BACKGROUND_MUTATION_ROUTE:'mac16'}).enabled,false) })
