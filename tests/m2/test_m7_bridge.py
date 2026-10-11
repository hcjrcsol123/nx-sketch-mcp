"""Real bridge/helper/host lifecycle tests; NX-free geometry fixture, not NX acceptance."""
from __future__ import annotations
import unittest
import uuid
import time
import test_m3_bridge as m3
from test_m2_bridge import successful_helper_result, successful_tool_result, call_tool
from test_mcp_stdio import extract_structured_result


class M7BridgeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        m3.M3BridgeSubsystemTests.setUpClass.__func__(cls)
    @classmethod
    def tearDownClass(cls):
        m3.M3BridgeSubsystemTests.tearDownClass.__func__(cls)

    setUp = m3.M3BridgeSubsystemTests.setUp
    start_harness = m3.M3BridgeSubsystemTests.start_harness
    start_helper = m3.M3BridgeSubsystemTests.start_helper
    start_host = m3.M3BridgeSubsystemTests.start_host
    helper_call = m3.M3BridgeSubsystemTests.helper_call
    harness_command = m3.M3BridgeSubsystemTests.harness_command
    fake_counts = m3.M3BridgeSubsystemTests.fake_counts
    get_operation = m3.M3BridgeSubsystemTests.get_operation
    wait_operation = m3.M3BridgeSubsystemTests.wait_operation
    release_fixture_operation = m3.M3BridgeSubsystemTests.release_fixture_operation
    _cleanup_harness = m3.M3BridgeSubsystemTests._cleanup_harness
    _cleanup_child = staticmethod(m3.M3BridgeSubsystemTests._cleanup_child)
    _stop_harness = m3.M3BridgeSubsystemTests._stop_harness
    takeover = m3.M3BridgeSubsystemTests.takeover
    prepare_sketch_args = m3.M3BridgeSubsystemTests.prepare_sketch_args
    prepare_extrude_args = m3.M3BridgeSubsystemTests.prepare_extrude_args
    create_sketch = m3.M3BridgeSubsystemTests.create_sketch
    prepare_extrude = m3.M3BridgeSubsystemTests.prepare_extrude
    execute_plan = m3.M3BridgeSubsystemTests.execute_plan
    verify_plan = m3.M3BridgeSubsystemTests.verify_plan

    def seed(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10)
        grant = self.takeover(ready, host=host)
        owner, epoch = grant['ownership_id'], grant['binding_epoch']
        sketch = self.create_sketch(helper, owner, epoch, [
            {'id': 'cylinder', 'type': 'circle', 'center_mm': [0, 0], 'diameter_mm': 40}])
        _, _, prepared = self.prepare_extrude(helper, owner, epoch, sketch['result']['sketch_id'],
            mode='create', direction='positive', extent='finite', depth_mm=100)
        _, _, done = self.execute_plan(helper, owner, epoch, prepared['result']['plan']['plan_id'])
        self.assertEqual(done['state'], 'SUCCEEDED', done)
        return helper, host, owner, epoch, done['result']['body_id']

    def args(self, helper, owner, epoch, target, kind='thread'):
        session = successful_helper_result(self, self.helper_call(helper, 'model.get_session',
            {'ownership_id': owner, 'binding_epoch': epoch}))
        args = dict(operation_id='m7-' + uuid.uuid4().hex, ownership_id=owner, binding_epoch=epoch,
            revision_id=session['revision_id'], target_body_id=target, axis='Z', axis_origin_mm=[0, 0, 0],
            start_mm=100, length_mm=30, handedness='right')
        if kind == 'thread':
            args.update(direction='negative', major_diameter_mm=40, minor_diameter_mm=38, pitch_mm=2)
        else:
            args.update(start_mm=20, length_mm=60, lead_mm=20, root_diameter_mm=30,
                reference_diameter_mm=40, outer_diameter_mm=50, space_width_mm=6, flank_angle_deg=20, phase_deg=0)
        return args

    def prepare(self, helper, host, args, kind):
        accepted = successful_tool_result(self, call_tool(host, 'nx_prepare_' + kind, args))
        done = self.wait_operation(helper, args['operation_id'])
        self.assertEqual(done['state'], 'SUCCEEDED', done)
        self.assertEqual(done['result']['plan']['kind'], 'create_' + kind)
        self.assertEqual(done['result']['plan']['body_id'], args['target_body_id'])
        return done['result']['plan']['plan_id']

    def test_both_native_plans_roundtrip_with_ownership_rotation(self):
        helper, host, owner, epoch, target = self.seed()
        capabilities = extract_structured_result(call_tool(host, 'nx_get_capabilities', {})['result'])
        self.assertTrue(capabilities['model_helical_adapter_available'])
        self.assertTrue(capabilities['model_helical_write_enabled'])
        for kind in ('thread', 'helical_groove'):
            args = self.args(helper, owner, epoch, target, kind)
            pid = self.prepare(helper, host, args, kind)
            again = successful_tool_result(self, call_tool(host, 'nx_prepare_' + kind, args))
            self.assertEqual(again['result']['plan']['plan_id'], pid)
            conflict_args = dict(args, length_mm=args['length_mm'] + 1)
            conflict = call_tool(host, 'nx_prepare_' + kind, conflict_args)
            self.assertTrue(conflict['result']['isError'], conflict)
            eid, _, done = self.execute_plan(helper, owner, epoch, pid)
            self.assertEqual(done['state'], 'SUCCEEDED', done)
            self.assertEqual(done['result']['kind'], 'create_' + kind)
            self.verify_plan(helper, host, owner, epoch, eid)
            reused = self.helper_call(helper, 'operation.execute_plan', dict(
                operation_id='m7-reuse-' + uuid.uuid4().hex, ownership_id=owner, binding_epoch=epoch, plan_id=pid))
            self.assertEqual(reused['error']['code'], 'PLAN_ALREADY_USED', reused)
        oid = 'm7-save-' + uuid.uuid4().hex
        accepted = successful_tool_result(self, call_tool(host, 'nx_save_copy', dict(operation_id=oid,
            ownership_id=owner, binding_epoch=epoch, target_path=str(self.temp_path / 'helical_saved.prt'), reopen=True)))
        done = self.wait_operation(helper, oid)
        self.assertEqual(done['state'], 'SUCCEEDED', done)
        self.assertNotEqual(done['result']['new_ownership_id'], owner)
        self.assertGreater(done['result']['new_binding_epoch'], epoch)

    def test_direct_bridge_rejects_invalid_inputs_before_nx_dispatch(self):
        helper, host, owner, epoch, target = self.seed()
        before = successful_helper_result(self, self.helper_call(helper, 'bridge.status', {}))['nx_api_calls']
        thread = self.args(helper, owner, epoch, target)
        groove = self.args(helper, owner, epoch, target, 'helical_groove')
        invalid = [
            ('thread', dict(thread, major_diameter_mm=True)),
            ('thread', dict(thread, minor_diameter_mm=40)),
            ('thread', dict(thread, axis='Q')),
            ('thread', dict(thread, length_mm=1001, pitch_mm=2)),
            ('thread', dict(thread, length_mm=1000001)),
            ('thread', dict(thread, axis_origin_mm=[False, 0, 0])),
            ('thread', dict(thread, extra='no')),
            ('helical_groove', dict(groove, root_diameter_mm=40)),
            ('helical_groove', dict(groove, space_width_mm=20)),
            ('helical_groove', dict(groove, space_width_mm=0.1)),
            ('helical_groove', dict(groove, flank_angle_deg=45)),
            ('helical_groove', dict(groove, phase_deg=361)),
            ('helical_groove', dict(groove, length_mm=1001)),
        ]
        for kind, args in invalid:
            with self.subTest(kind=kind, args=args):
                args['operation_id'] = 'invalid-' + uuid.uuid4().hex
                reply = self.helper_call(helper, 'operation.prepare_' + kind, args)
                self.assertEqual(reply['error']['code'], 'INVALID_REQUEST', reply)
        after = successful_helper_result(self, self.helper_call(helper, 'bridge.status', {}))['nx_api_calls']
        self.assertEqual(after, before)

    def test_stale_plan_and_rollback_then_unknown_freeze(self):
        helper, host, owner, epoch, target = self.seed()
        first = self.prepare(helper, host, self.args(helper, owner, epoch, target), 'thread')
        second = self.prepare(helper, host, self.args(helper, owner, epoch, target, 'helical_groove'), 'helical_groove')
        _, _, done = self.execute_plan(helper, owner, epoch, first)
        self.assertEqual(done['state'], 'SUCCEEDED', done)
        _, _, stale = self.execute_plan(helper, owner, epoch, second)
        self.assertEqual(stale['state'], 'FAILED', stale)
        self.assertEqual(stale['error']['code'], 'STALE_PLAN', stale)
        pid = self.prepare(helper, host, self.args(helper, owner, epoch, target), 'thread')
        self.harness_command('rollback-after-write')
        _, _, rolled = self.execute_plan(helper, owner, epoch, pid)
        self.assertEqual(rolled['state'], 'FAILED', rolled)
        self.assertEqual(rolled['result']['mutation_state'], 'ROLLED_BACK', rolled)
        pid = self.prepare(helper, host, self.args(helper, owner, epoch, target), 'thread')
        self.harness_command('unknown-after-mutation')
        _, _, unknown = self.execute_plan(helper, owner, epoch, pid)
        self.assertEqual(unknown['state'], 'UNKNOWN', unknown)
        status = successful_helper_result(self, self.helper_call(helper, 'bridge.status', {}))
        self.assertTrue(status['writes_halted'])
        self.assertFalse(status['model_helical_write_enabled'])

    def test_late_native_completion_freezes_without_intermediate_poll(self):
        helper, host, owner, epoch, target = self.seed()
        pid = self.prepare(helper, host, self.args(helper, owner, epoch, target), 'thread')
        self.addCleanup(self.release_fixture_operation)
        self.harness_command('block-operation')
        oid = 'late-m7-' + uuid.uuid4().hex
        successful_helper_result(self, self.helper_call(helper, 'operation.execute_plan', dict(
            operation_id=oid, ownership_id=owner, binding_epoch=epoch, plan_id=pid)))
        self.assertTrue(self.harness_command('wait-operation-start')['observed'])
        self.assertTrue(self.harness_command('age-running-m3-operation')['observed'])
        self.harness_command('release-operation')
        # No bridge status/get calls until the adapter has returned its late result.
        time.sleep(0.2)
        done = self.get_operation(helper, oid)
        self.assertEqual(done['state'], 'UNKNOWN', done)
        self.assertEqual(done['error']['code'], 'OPERATION_EXECUTION_TIMEOUT', done)
        self.assertEqual(done['late_completion']['mutation_state'], 'APPLIED', done)

if __name__ == '__main__':
    unittest.main()
