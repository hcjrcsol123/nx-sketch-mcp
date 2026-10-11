"""Offline checks for unit conversion expectations in plan section 7.2.

Protocol keeps explicit quantities {value, unit}; geometry layer uses SI (m, rad);
the NX adapter converts to target API units. This test only checks the conversion
constants and a few round trips; it does not import or call NX.

Evidence level: offline unit test only.
"""

import math
import unittest

MM_PER_INCH = 25.4
M_PER_MM = 0.001


def mm_to_m(value_mm):
    return value_mm * M_PER_MM


def m_to_mm(value_m):
    return value_m / M_PER_MM


def inch_to_mm(value_in):
    return value_in * MM_PER_INCH


def mm_to_inch(value_mm):
    return value_mm / MM_PER_INCH


def deg_to_rad(value_deg):
    return value_deg * math.pi / 180.0


class TestUnitConversion(unittest.TestCase):
    def test_mm_m_round_trip(self):
        self.assertAlmostEqual(mm_to_m(10.0), 0.01, places=12)
        self.assertAlmostEqual(m_to_mm(0.01), 10.0, places=12)

    def test_inch_mm(self):
        self.assertAlmostEqual(inch_to_mm(1.0), 25.4, places=12)
        self.assertAlmostEqual(mm_to_inch(25.4), 1.0, places=12)

    def test_angle(self):
        self.assertAlmostEqual(deg_to_rad(180.0), math.pi, places=12)
        self.assertAlmostEqual(deg_to_rad(90.0), math.pi / 2.0, places=12)

    def test_case_a_dimensions_are_mm_based(self):
        # Plan section 10 examples are in mm; verify the SI representation used by the geometry layer.
        self.assertAlmostEqual(mm_to_m(100.0), 0.1, places=12)
        self.assertAlmostEqual(mm_to_m(0.01), 0.00001, places=15)


if __name__ == "__main__":
    unittest.main()
