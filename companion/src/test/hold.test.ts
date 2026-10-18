import {test} from 'node:test'
import assert from 'node:assert/strict'
import {HoldGesture} from '../hold.js'
test('short release toggles once; three-second hold releases as undo only', () => {
  let now = 0; const hold = new HoldGesture(() => now)
  hold.down('a'); now = 2999; assert.equal(hold.up('a'), 'toggle'); assert.equal(hold.up('a'), undefined)
  hold.down('a'); now += 3000; assert.equal(hold.hint, 'RELEASE TO REVERT'); assert.equal(hold.up('a'), 'undo')
})
test('unmatched releases, stale holds and lost release followed by new down never undo accidentally', () => {
  let now = 0; const hold = new HoldGesture(() => now)
  assert.equal(hold.up('a'), undefined)
  hold.down('a'); now = 3500; assert.equal(hold.up('b'), undefined)
  hold.down('a'); now += 100; assert.equal(hold.up('a'), 'toggle')
  hold.down('a'); now += 60001; assert.equal(hold.up('a'), undefined)
  hold.down('a'); now += 4000; hold.reset(); assert.equal(hold.up('a'), undefined)
})
