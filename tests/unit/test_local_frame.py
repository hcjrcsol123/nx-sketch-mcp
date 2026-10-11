"""Offline checks for sketch local-frame arithmetic.

Algorithm:
    n = normalize(z)
    p = v - (v . n) n
    x = normalize(p)
    y = n x x
and the result must be a unit orthonormal right-handed basis.

The degeneracy threshold is a PROVISIONAL arithmetic-test input;
it is not a production geometry acceptance threshold.

This file does not import or call NX. Evidence level: offline unit test only.
"""

import math
import unittest

PROVISIONAL_DEGENERACY_EPS = 1e-12  # PROVISIONAL - not a frozen acceptance threshold


class DegenerateProjectionError(ValueError):
    pass


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def norm(a):
    return math.sqrt(dot(a, a))


def normalize(a, eps=PROVISIONAL_DEGENERACY_EPS):
    # Plan section 5.4 requires checking finite values before normalization.
    for component in a:
        if not math.isfinite(component):
            raise DegenerateProjectionError("non-finite direction component")
    length = norm(a)
    if length <= eps:
        raise DegenerateProjectionError("zero-length direction")
    return (a[0] / length, a[1] / length, a[2] / length)


def cross(a, b):
    return (
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    )


def make_frame(z, v, eps=PROVISIONAL_DEGENERACY_EPS):
    n = normalize(z, eps)
    v_dot_n = dot(v, n)
    p = (v[0] - v_dot_n * n[0], v[1] - v_dot_n * n[1], v[2] - v_dot_n * n[2])
    if norm(p) <= eps:
        raise DegenerateProjectionError("reference direction parallel to normal; declare fallback")
    x = normalize(p, eps)
    y = cross(n, x)
    return n, x, y


class TestLocalFrame(unittest.TestCase):
    def assertVectorAlmostEqual(self, actual, expected, places=12):
        for a, e in zip(actual, expected):
            self.assertAlmostEqual(a, e, places=places)

    def test_axis_aligned(self):
        n, x, y = make_frame((0.0, 0.0, 1.0), (1.0, 0.0, 0.0))
        self.assertVectorAlmostEqual(n, (0.0, 0.0, 1.0))
        self.assertVectorAlmostEqual(x, (1.0, 0.0, 0.0))
        self.assertVectorAlmostEqual(y, (0.0, 1.0, 0.0))

    def test_non_unit_normal_is_normalized(self):
        n, x, y = make_frame((0.0, 0.0, 2.5), (1.0, 0.0, 0.0))
        self.assertVectorAlmostEqual(n, (0.0, 0.0, 1.0))
        self.assertVectorAlmostEqual(x, (1.0, 0.0, 0.0))

    def test_reference_not_perpendicular_is_projected(self):
        n, x, y = make_frame((0.0, 0.0, 1.0), (1.0, 0.0, 1.0))
        self.assertVectorAlmostEqual(n, (0.0, 0.0, 1.0))
        self.assertVectorAlmostEqual(x, (1.0, 0.0, 0.0))
        self.assertVectorAlmostEqual(y, (0.0, 1.0, 0.0))

    def test_degenerate_projection_raises(self):
        with self.assertRaises(DegenerateProjectionError):
            make_frame((0.0, 0.0, 1.0), (0.0, 0.0, 3.0))

    def test_non_finite_components_raise(self):
        # Plan section 5.4 requires finite-value checks before normalization (T16 rejects NaN/Inf).
        for bad in ((float('nan'), 0.0, 0.0), (float('inf'), 0.0, 0.0), (0.0, float('-inf'), 0.0)):
            with self.assertRaises(DegenerateProjectionError):
                make_frame(bad, (1.0, 0.0, 0.0))
            with self.assertRaises(DegenerateProjectionError):
                make_frame((0.0, 0.0, 1.0), bad)

    def test_orthonormal_right_handed_tilted(self):
        n, x, y = make_frame((1.0, 2.0, 3.0), (0.0, 1.0, 0.0))
        for vec in (n, x, y):
            self.assertAlmostEqual(norm(vec), 1.0, places=12)
        self.assertAlmostEqual(dot(n, x), 0.0, places=12)
        self.assertAlmostEqual(dot(n, y), 0.0, places=12)
        self.assertAlmostEqual(dot(x, y), 0.0, places=12)
        # right-handed: x cross y = n
        self.assertVectorAlmostEqual(cross(x, y), n)

    def test_direction_normalization_does_not_scale_geometry(self):
        # A direction of length 100 must produce the same frame as length 1.
        frame_a = make_frame((0.0, 0.0, 100.0), (1.0, 0.0, 0.0))
        frame_b = make_frame((0.0, 0.0, 1.0), (1.0, 0.0, 0.0))
        for va, vb in zip(frame_a, frame_b):
            self.assertVectorAlmostEqual(va, vb)


if __name__ == "__main__":
    unittest.main()
