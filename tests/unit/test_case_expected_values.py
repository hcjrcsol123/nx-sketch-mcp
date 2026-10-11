"""Offline expected-value checks for plate and hole geometry.

These are closed-form arithmetic expectations, NOT test results or measurements.
This file does not import or call NX. Evidence level: offline unit test only.

Run: python -m unittest tests.unit.test_case_expected_values -v   (from project root)
"""

import math
import unittest

# --- Case A: plate L x W x T with through hole D -------------------------------------------
# V(L, W, T, D) = (L*W - pi*D^2/4) * T
def plate_with_hole_volume(L, W, T, D):
    return (L * W - math.pi * D * D / 4.0) * T


class TestCaseAExpectedValues(unittest.TestCase):
    def test_v0_initial(self):
        # L=100, W=60, T=10, D=10
        self.assertAlmostEqual(plate_with_hole_volume(100.0, 60.0, 10.0, 10.0), 59214.6018366, places=6)

    def test_v1_after_L120(self):
        self.assertAlmostEqual(plate_with_hole_volume(120.0, 60.0, 10.0, 10.0), 71214.6018366, places=6)

    def test_v2_after_T12(self):
        self.assertAlmostEqual(plate_with_hole_volume(120.0, 60.0, 12.0, 10.0), 85457.5222039, places=6)

    def test_p06_after_L140(self):
        self.assertAlmostEqual(plate_with_hole_volume(140.0, 60.0, 12.0, 10.0), 99857.5222039, places=6)

    def test_hole_centers(self):
        # Hole center follows L/2, W/2 on the top face at z = T.
        centers = [
            (100.0, 60.0, 10.0, (50.0, 30.0, 10.0)),
            (120.0, 60.0, 10.0, (60.0, 30.0, 10.0)),
            (120.0, 60.0, 12.0, (60.0, 30.0, 12.0)),
            (140.0, 60.0, 12.0, (70.0, 30.0, 12.0)),
        ]
        for L, W, T, expected in centers:
            actual = (L / 2.0, W / 2.0, T)
            self.assertEqual(actual, expected)

    def test_bounding_boxes(self):
        boxes = [
            (100.0, 60.0, 10.0, (100.0, 60.0, 10.0)),
            (120.0, 60.0, 10.0, (120.0, 60.0, 10.0)),
            (120.0, 60.0, 12.0, (120.0, 60.0, 12.0)),
            (140.0, 60.0, 12.0, (140.0, 60.0, 12.0)),
        ]
        for L, W, T, expected in boxes:
            self.assertEqual((L, W, T), expected)


# --- Case B: external history part ----------------------------------------------------------
class TestCaseBExpectedValues(unittest.TestCase):
    def test_volumes(self):
        base = 80.0 * 40.0 * 6.0
        after_width50 = 80.0 * 50.0 * 6.0
        boss = 20.0 * 10.0 * 5.0
        self.assertAlmostEqual(base, 19200.0, places=6)
        self.assertAlmostEqual(after_width50, 24000.0, places=6)
        self.assertAlmostEqual(after_width50 + boss, 25000.0, places=6)

    def test_max_z(self):
        base_top_z = 6.0
        boss_height = 5.0
        self.assertAlmostEqual(base_top_z + boss_height, 11.0, places=6)


if __name__ == "__main__":
    unittest.main()
