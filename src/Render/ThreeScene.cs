// Engine-free stand-ins for the three.js r185 math and scene-graph types that decide the terrain's light, shadow-camera,
// backdrop and world-yaw values. Ported literally from three@0.185.1 (`src/math/{Vector2,Vector3,Matrix3,Matrix4,
// Quaternion,Euler}.js`, `src/core/Object3D.js`, `src/cameras/{Camera,OrthographicCamera,PerspectiveCamera}.js`,
// `src/lights/{Light,AmbientLight,HemisphereLight,DirectionalLight,PointLight,LightShadow,DirectionalLightShadow,
// PointLightShadow}.js`) — only the members the terrain presentation port (TerrainPresentationState) exercises.
//
// PORT NOTES
// * Every value three stores in a plain JS array / number field stays `double` (Matrix4.elements is a JS Array, not a
//   Float32Array), so every matrix/vector here is bit-comparable with the numbers `scene-dump.ts` serialises.
// * `Math.cos/sin/tan` go through the JsMath alias (the browser's libm, see JsMath.Node20Semantics); sqrt is exact.
// * ThreeVector3 / ThreeColor are owned by ThreeValues.cs (another agent's file). Their missing three.js members are
//   added here as C# extension methods with the original names, so call sites read exactly like three.
// * Event dispatch (`added`/`removed`), `userData`, `uuid` and serialisation are not ported: nothing value-producing
//   reads them.
// * Object3D keeps three's Euler ↔ quaternion coupling in one direction only: `rotation.set(...)` updates the quaternion
//   (`onRotationChange`). The reverse callback (`quaternion` change → `rotation.setFromQuaternion`) only refreshes the
//   Euler mirror, which no ported code reads, so it is omitted.
// * Main-thread-only render state (one live terrain layer per process); the module scratch vectors/matrices three keeps
//   at module level are plain statics here — never touch these types from a worker thread.
using System;
using System.Collections.Generic;
using Fluitown.Runtime;
using Math = Fluitown.Runtime.JsMath;

namespace Fluitown.Render;

/// <summary>three.js `constants.js` values used by the terrain layer.</summary>
public static class ThreeConstants
{
    public const int FrontSide = 0;
    public const int DoubleSide = 2;
    public const int PCFShadowMap = 1;
    public const int PCFSoftShadowMap = 2;

    public const int WebGLCoordinateSystem = 2000;
    public const int WebGPUCoordinateSystem = 2001;

    /// <summary>`MathUtils.DEG2RAD`.</summary>
    public const double DEG2RAD = Math.PI / 180;
}

/// <summary>three.js `Vector2`.</summary>
public sealed class ThreeVector2
{
    public double x;
    public double y;

    public ThreeVector2(double x = 0, double y = 0)
    {
        this.x = x;
        this.y = y;
    }

    public ThreeVector2 set(double x, double y)
    {
        this.x = x;
        this.y = y;
        return this;
    }

    public ThreeVector2 copy(ThreeVector2 v)
    {
        this.x = v.x;
        this.y = v.y;
        return this;
    }

    public ThreeVector2 multiply(ThreeVector2 v)
    {
        this.x *= v.x;
        this.y *= v.y;
        return this;
    }
}

/// <summary>three.js `Vector3` members missing from ThreeValues.cs, as extension methods with the original names.</summary>
public static class ThreeVector3Extensions
{
    public static ThreeVector3 sub(this ThreeVector3 self, ThreeVector3 v)
    {
        self.x -= v.x;
        self.y -= v.y;
        self.z -= v.z;
        return self;
    }

    public static ThreeVector3 subVectors(this ThreeVector3 self, ThreeVector3 a, ThreeVector3 b)
    {
        self.x = a.x - b.x;
        self.y = a.y - b.y;
        self.z = a.z - b.z;
        return self;
    }

    public static ThreeVector3 multiplyScalar(this ThreeVector3 self, double scalar)
    {
        self.x *= scalar;
        self.y *= scalar;
        self.z *= scalar;
        return self;
    }

    public static ThreeVector3 divideScalar(this ThreeVector3 self, double scalar)
    {
        return self.multiplyScalar(1 / scalar);
    }

    public static ThreeVector3 applyMatrix4(this ThreeVector3 self, ThreeMatrix4 m)
    {
        double x = self.x, y = self.y, z = self.z;
        double[] e = m.elements;

        double w = 1 / (e[3] * x + e[7] * y + e[11] * z + e[15]);

        self.x = (e[0] * x + e[4] * y + e[8] * z + e[12]) * w;
        self.y = (e[1] * x + e[5] * y + e[9] * z + e[13]) * w;
        self.z = (e[2] * x + e[6] * y + e[10] * z + e[14]) * w;

        return self;
    }

    /// <summary>Input: a 4×4 matrix whose upper-left 3×3 is a pure rotation (three's own precondition).</summary>
    public static ThreeVector3 transformDirection(this ThreeVector3 self, ThreeMatrix4 m)
    {
        double x = self.x, y = self.y, z = self.z;
        double[] e = m.elements;

        self.x = e[0] * x + e[4] * y + e[8] * z;
        self.y = e[1] * x + e[5] * y + e[9] * z;
        self.z = e[2] * x + e[6] * y + e[10] * z;

        return self.normalize();
    }

    public static double lengthSq(this ThreeVector3 self) => self.x * self.x + self.y * self.y + self.z * self.z;

    public static double length(this ThreeVector3 self) => Math.sqrt(self.x * self.x + self.y * self.y + self.z * self.z);

    /// <summary>`divideScalar(this.length() || 1)` — a zero (or NaN) length divides by 1.</summary>
    public static ThreeVector3 normalize(this ThreeVector3 self)
    {
        double length = self.length();
        return self.divideScalar(Js.Truthy(length) ? length : 1);
    }

    public static ThreeVector3 crossVectors(this ThreeVector3 self, ThreeVector3 a, ThreeVector3 b)
    {
        double ax = a.x, ay = a.y, az = a.z;
        double bx = b.x, by = b.y, bz = b.z;

        self.x = ay * bz - az * by;
        self.y = az * bx - ax * bz;
        self.z = ax * by - ay * bx;

        return self;
    }

    public static ThreeVector3 setFromMatrixPosition(this ThreeVector3 self, ThreeMatrix4 m)
    {
        double[] e = m.elements;

        self.x = e[12];
        self.y = e[13];
        self.z = e[14];

        return self;
    }

    public static ThreeVector3 setFromMatrixColumn(this ThreeVector3 self, ThreeMatrix4 m, int index)
    {
        return self.fromArray(m.elements, index * 4);
    }

    public static ThreeVector3 fromArray(this ThreeVector3 self, double[] array, int offset = 0)
    {
        self.x = array[offset];
        self.y = array[offset + 1];
        self.z = array[offset + 2];
        return self;
    }
}

/// <summary>three.js `Color` members missing from ThreeValues.cs, as extension methods with the original names.</summary>
public static class ThreeColorExtensions
{
    public static ThreeColor multiplyScalar(this ThreeColor self, double s)
    {
        self.r *= s;
        self.g *= s;
        self.b *= s;
        return self;
    }

    public static ThreeColor lerp(this ThreeColor self, ThreeColor color, double alpha)
    {
        self.r += (color.r - self.r) * alpha;
        self.g += (color.g - self.g) * alpha;
        self.b += (color.b - self.b) * alpha;
        return self;
    }
}

/// <summary>three.js `Matrix3` (column-major `elements`, identity by default). Only used as texture-transform uniforms.</summary>
public sealed class ThreeMatrix3
{
    public readonly double[] elements = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
}

/// <summary>three.js `Matrix4`. `elements` is column-major exactly like three (`te[column * 4 + row]`).</summary>
public sealed class ThreeMatrix4
{
    public readonly double[] elements = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    // three's module-level scratch (`_v1`, `_m1`, `_zero`, `_one`, `_x`, `_y`, `_z`). Main-thread-only (see file header).
    private static readonly ThreeVector3 _v1 = new ThreeVector3();
    private static readonly ThreeMatrix4 _m1 = new ThreeMatrix4();
    private static readonly ThreeVector3 _x = new ThreeVector3();
    private static readonly ThreeVector3 _y = new ThreeVector3();
    private static readonly ThreeVector3 _z = new ThreeVector3();

    /// <summary>Row-major arguments, like three (`set(n11, n12, …, n44)`).</summary>
    public ThreeMatrix4 set(
        double n11, double n12, double n13, double n14,
        double n21, double n22, double n23, double n24,
        double n31, double n32, double n33, double n34,
        double n41, double n42, double n43, double n44)
    {
        double[] te = this.elements;

        te[0] = n11; te[4] = n12; te[8] = n13; te[12] = n14;
        te[1] = n21; te[5] = n22; te[9] = n23; te[13] = n24;
        te[2] = n31; te[6] = n32; te[10] = n33; te[14] = n34;
        te[3] = n41; te[7] = n42; te[11] = n43; te[15] = n44;

        return this;
    }

    public ThreeMatrix4 identity()
    {
        this.set(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1);

        return this;
    }

    public ThreeMatrix4 copy(ThreeMatrix4 m)
    {
        double[] te = this.elements;
        double[] me = m.elements;

        te[0] = me[0]; te[1] = me[1]; te[2] = me[2]; te[3] = me[3];
        te[4] = me[4]; te[5] = me[5]; te[6] = me[6]; te[7] = me[7];
        te[8] = me[8]; te[9] = me[9]; te[10] = me[10]; te[11] = me[11];
        te[12] = me[12]; te[13] = me[13]; te[14] = me[14]; te[15] = me[15];

        return this;
    }

    public ThreeMatrix4 extractRotation(ThreeMatrix4 m)
    {
        if (m.determinantAffine() == 0)
        {
            return this.identity();
        }

        double[] te = this.elements;
        double[] me = m.elements;

        double scaleX = 1 / _v1.setFromMatrixColumn(m, 0).length();
        double scaleY = 1 / _v1.setFromMatrixColumn(m, 1).length();
        double scaleZ = 1 / _v1.setFromMatrixColumn(m, 2).length();

        te[0] = me[0] * scaleX;
        te[1] = me[1] * scaleX;
        te[2] = me[2] * scaleX;
        te[3] = 0;

        te[4] = me[4] * scaleY;
        te[5] = me[5] * scaleY;
        te[6] = me[6] * scaleY;
        te[7] = 0;

        te[8] = me[8] * scaleZ;
        te[9] = me[9] * scaleZ;
        te[10] = me[10] * scaleZ;
        te[11] = 0;

        te[12] = 0;
        te[13] = 0;
        te[14] = 0;
        te[15] = 1;

        return this;
    }

    public ThreeMatrix4 lookAt(ThreeVector3 eye, ThreeVector3 target, ThreeVector3 up)
    {
        double[] te = this.elements;

        _z.subVectors(eye, target);

        if (_z.lengthSq() == 0)
        {
            // eye and target are in the same position
            _z.z = 1;
        }

        _z.normalize();
        _x.crossVectors(up, _z);

        if (_x.lengthSq() == 0)
        {
            // up and z are parallel
            if (Math.abs(up.z) == 1)
            {
                _z.x += 0.0001;
            }
            else
            {
                _z.z += 0.0001;
            }

            _z.normalize();
            _x.crossVectors(up, _z);
        }

        _x.normalize();
        _y.crossVectors(_z, _x);

        te[0] = _x.x; te[4] = _y.x; te[8] = _z.x;
        te[1] = _x.y; te[5] = _y.y; te[9] = _z.y;
        te[2] = _x.z; te[6] = _y.z; te[10] = _z.z;

        return this;
    }

    public ThreeMatrix4 multiply(ThreeMatrix4 m) => this.multiplyMatrices(this, m);

    public ThreeMatrix4 multiplyMatrices(ThreeMatrix4 a, ThreeMatrix4 b)
    {
        double[] ae = a.elements;
        double[] be = b.elements;
        double[] te = this.elements;

        double a11 = ae[0], a12 = ae[4], a13 = ae[8], a14 = ae[12];
        double a21 = ae[1], a22 = ae[5], a23 = ae[9], a24 = ae[13];
        double a31 = ae[2], a32 = ae[6], a33 = ae[10], a34 = ae[14];
        double a41 = ae[3], a42 = ae[7], a43 = ae[11], a44 = ae[15];

        double b11 = be[0], b12 = be[4], b13 = be[8], b14 = be[12];
        double b21 = be[1], b22 = be[5], b23 = be[9], b24 = be[13];
        double b31 = be[2], b32 = be[6], b33 = be[10], b34 = be[14];
        double b41 = be[3], b42 = be[7], b43 = be[11], b44 = be[15];

        te[0] = a11 * b11 + a12 * b21 + a13 * b31 + a14 * b41;
        te[4] = a11 * b12 + a12 * b22 + a13 * b32 + a14 * b42;
        te[8] = a11 * b13 + a12 * b23 + a13 * b33 + a14 * b43;
        te[12] = a11 * b14 + a12 * b24 + a13 * b34 + a14 * b44;

        te[1] = a21 * b11 + a22 * b21 + a23 * b31 + a24 * b41;
        te[5] = a21 * b12 + a22 * b22 + a23 * b32 + a24 * b42;
        te[9] = a21 * b13 + a22 * b23 + a23 * b33 + a24 * b43;
        te[13] = a21 * b14 + a22 * b24 + a23 * b34 + a24 * b44;

        te[2] = a31 * b11 + a32 * b21 + a33 * b31 + a34 * b41;
        te[6] = a31 * b12 + a32 * b22 + a33 * b32 + a34 * b42;
        te[10] = a31 * b13 + a32 * b23 + a33 * b33 + a34 * b43;
        te[14] = a31 * b14 + a32 * b24 + a33 * b34 + a34 * b44;

        te[3] = a41 * b11 + a42 * b21 + a43 * b31 + a44 * b41;
        te[7] = a41 * b12 + a42 * b22 + a43 * b32 + a44 * b42;
        te[11] = a41 * b13 + a42 * b23 + a43 * b33 + a44 * b43;
        te[15] = a41 * b14 + a42 * b24 + a43 * b34 + a44 * b44;

        return this;
    }

    public double determinantAffine()
    {
        double[] te = this.elements;

        double n11 = te[0], n12 = te[4], n13 = te[8];
        double n21 = te[1], n22 = te[5], n23 = te[9];
        double n31 = te[2], n32 = te[6], n33 = te[10];

        return n11 * (n22 * n33 - n23 * n32) -
            n12 * (n21 * n33 - n23 * n31) +
            n13 * (n21 * n32 - n22 * n31);
    }

    public ThreeMatrix4 invert()
    {
        // based on https://github.com/toji/gl-matrix
        double[] te = this.elements;

        double n11 = te[0], n21 = te[1], n31 = te[2], n41 = te[3],
            n12 = te[4], n22 = te[5], n32 = te[6], n42 = te[7],
            n13 = te[8], n23 = te[9], n33 = te[10], n43 = te[11],
            n14 = te[12], n24 = te[13], n34 = te[14], n44 = te[15],

            t1 = n11 * n22 - n21 * n12,
            t2 = n11 * n32 - n31 * n12,
            t3 = n11 * n42 - n41 * n12,
            t4 = n21 * n32 - n31 * n22,
            t5 = n21 * n42 - n41 * n22,
            t6 = n31 * n42 - n41 * n32,
            t7 = n13 * n24 - n23 * n14,
            t8 = n13 * n34 - n33 * n14,
            t9 = n13 * n44 - n43 * n14,
            t10 = n23 * n34 - n33 * n24,
            t11 = n23 * n44 - n43 * n24,
            t12 = n33 * n44 - n43 * n34;

        double det = t1 * t12 - t2 * t11 + t3 * t10 + t4 * t9 - t5 * t8 + t6 * t7;

        if (det == 0) return this.set(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        double detInv = 1 / det;

        te[0] = (n22 * t12 - n32 * t11 + n42 * t10) * detInv;
        te[1] = (n31 * t11 - n21 * t12 - n41 * t10) * detInv;
        te[2] = (n24 * t6 - n34 * t5 + n44 * t4) * detInv;
        te[3] = (n33 * t5 - n23 * t6 - n43 * t4) * detInv;

        te[4] = (n32 * t9 - n12 * t12 - n42 * t8) * detInv;
        te[5] = (n11 * t12 - n31 * t9 + n41 * t8) * detInv;
        te[6] = (n34 * t3 - n14 * t6 - n44 * t2) * detInv;
        te[7] = (n13 * t6 - n33 * t3 + n43 * t2) * detInv;

        te[8] = (n12 * t11 - n22 * t9 + n42 * t7) * detInv;
        te[9] = (n21 * t9 - n11 * t11 - n41 * t7) * detInv;
        te[10] = (n14 * t5 - n24 * t3 + n44 * t1) * detInv;
        te[11] = (n23 * t3 - n13 * t5 - n43 * t1) * detInv;

        te[12] = (n22 * t8 - n12 * t10 - n32 * t7) * detInv;
        te[13] = (n11 * t10 - n21 * t8 + n31 * t7) * detInv;
        te[14] = (n24 * t2 - n14 * t4 - n34 * t1) * detInv;
        te[15] = (n13 * t4 - n23 * t2 + n33 * t1) * detInv;

        return this;
    }

    public ThreeMatrix4 compose(ThreeVector3 position, ThreeQuaternion quaternion, ThreeVector3 scale)
    {
        double[] te = this.elements;

        double x = quaternion._x, y = quaternion._y, z = quaternion._z, w = quaternion._w;
        double x2 = x + x, y2 = y + y, z2 = z + z;
        double xx = x * x2, xy = x * y2, xz = x * z2;
        double yy = y * y2, yz = y * z2, zz = z * z2;
        double wx = w * x2, wy = w * y2, wz = w * z2;

        double sx = scale.x, sy = scale.y, sz = scale.z;

        te[0] = (1 - (yy + zz)) * sx;
        te[1] = (xy + wz) * sx;
        te[2] = (xz - wy) * sx;
        te[3] = 0;

        te[4] = (xy - wz) * sy;
        te[5] = (1 - (xx + zz)) * sy;
        te[6] = (yz + wx) * sy;
        te[7] = 0;

        te[8] = (xz + wy) * sz;
        te[9] = (yz - wx) * sz;
        te[10] = (1 - (xx + yy)) * sz;
        te[11] = 0;

        te[12] = position.x;
        te[13] = position.y;
        te[14] = position.z;
        te[15] = 1;

        return this;
    }

    public ThreeMatrix4 decompose(ThreeVector3 position, ThreeQuaternion quaternion, ThreeVector3 scale)
    {
        double[] te = this.elements;

        position.x = te[12];
        position.y = te[13];
        position.z = te[14];

        double det = this.determinantAffine();

        if (det == 0)
        {
            scale.set(1, 1, 1);
            quaternion.identity();

            return this;
        }

        double sx = _v1.set(te[0], te[1], te[2]).length();
        double sy = _v1.set(te[4], te[5], te[6]).length();
        double sz = _v1.set(te[8], te[9], te[10]).length();

        // if determinant is negative, we need to invert one scale
        if (det < 0) sx = -sx;

        // scale the rotation part
        _m1.copy(this);

        double invSX = 1 / sx;
        double invSY = 1 / sy;
        double invSZ = 1 / sz;

        _m1.elements[0] *= invSX;
        _m1.elements[1] *= invSX;
        _m1.elements[2] *= invSX;

        _m1.elements[4] *= invSY;
        _m1.elements[5] *= invSY;
        _m1.elements[6] *= invSY;

        _m1.elements[8] *= invSZ;
        _m1.elements[9] *= invSZ;
        _m1.elements[10] *= invSZ;

        quaternion.setFromRotationMatrix(_m1);

        scale.x = sx;
        scale.y = sy;
        scale.z = sz;

        return this;
    }

    public ThreeMatrix4 makePerspective(
        double left, double right, double top, double bottom, double near, double far,
        int coordinateSystem = ThreeConstants.WebGLCoordinateSystem, bool reversedDepth = false)
    {
        double[] te = this.elements;

        double x = 2 * near / (right - left);
        double y = 2 * near / (top - bottom);

        double a = (right + left) / (right - left);
        double b = (top + bottom) / (top - bottom);

        double c, d;

        if (reversedDepth)
        {
            c = near / (far - near);
            d = (far * near) / (far - near);
        }
        else
        {
            if (coordinateSystem == ThreeConstants.WebGLCoordinateSystem)
            {
                c = -(far + near) / (far - near);
                d = (-2 * far * near) / (far - near);
            }
            else if (coordinateSystem == ThreeConstants.WebGPUCoordinateSystem)
            {
                c = -far / (far - near);
                d = (-far * near) / (far - near);
            }
            else
            {
                throw new InvalidOperationException("THREE.Matrix4.makePerspective(): Invalid coordinate system: " + coordinateSystem);
            }
        }

        te[0] = x; te[4] = 0; te[8] = a; te[12] = 0;
        te[1] = 0; te[5] = y; te[9] = b; te[13] = 0;
        te[2] = 0; te[6] = 0; te[10] = c; te[14] = d;
        te[3] = 0; te[7] = 0; te[11] = -1; te[15] = 0;

        return this;
    }

    public ThreeMatrix4 makeOrthographic(
        double left, double right, double top, double bottom, double near, double far,
        int coordinateSystem = ThreeConstants.WebGLCoordinateSystem, bool reversedDepth = false)
    {
        double[] te = this.elements;

        double x = 2 / (right - left);
        double y = 2 / (top - bottom);

        double a = -(right + left) / (right - left);
        double b = -(top + bottom) / (top - bottom);

        double c, d;

        if (reversedDepth)
        {
            c = 1 / (far - near);
            d = far / (far - near);
        }
        else
        {
            if (coordinateSystem == ThreeConstants.WebGLCoordinateSystem)
            {
                c = -2 / (far - near);
                d = -(far + near) / (far - near);
            }
            else if (coordinateSystem == ThreeConstants.WebGPUCoordinateSystem)
            {
                c = -1 / (far - near);
                d = -near / (far - near);
            }
            else
            {
                throw new InvalidOperationException("THREE.Matrix4.makeOrthographic(): Invalid coordinate system: " + coordinateSystem);
            }
        }

        te[0] = x; te[4] = 0; te[8] = 0; te[12] = a;
        te[1] = 0; te[5] = y; te[9] = 0; te[13] = b;
        te[2] = 0; te[6] = 0; te[10] = c; te[14] = d;
        te[3] = 0; te[7] = 0; te[11] = 0; te[15] = 1;

        return this;
    }
}

/// <summary>three.js `Quaternion`. `_x.._w` keep three's private names; `onChangeCallback` is `_onChangeCallback`.</summary>
public sealed class ThreeQuaternion
{
    public double _x;
    public double _y;
    public double _z;
    public double _w;

    internal Action _onChangeCallback = () => { };

    public ThreeQuaternion(double x = 0, double y = 0, double z = 0, double w = 1)
    {
        this._x = x;
        this._y = y;
        this._z = z;
        this._w = w;
    }

    public ThreeQuaternion set(double x, double y, double z, double w)
    {
        this._x = x;
        this._y = y;
        this._z = z;
        this._w = w;

        this._onChangeCallback();

        return this;
    }

    public ThreeQuaternion identity() => this.set(0, 0, 0, 1);

    public ThreeQuaternion setFromEuler(ThreeEuler euler, bool update = true)
    {
        double x = euler._x, y = euler._y, z = euler._z;
        string order = euler._order;

        // http://www.mathworks.com/matlabcentral/fileexchange/
        // 	20696-function-to-convert-between-dcm-euler-angles-quaternions-and-euler-vectors/
        //	content/SpinCalc.m

        double c1 = Math.cos(x / 2);
        double c2 = Math.cos(y / 2);
        double c3 = Math.cos(z / 2);

        double s1 = Math.sin(x / 2);
        double s2 = Math.sin(y / 2);
        double s3 = Math.sin(z / 2);

        switch (order)
        {
            case "XYZ":
                this._x = s1 * c2 * c3 + c1 * s2 * s3;
                this._y = c1 * s2 * c3 - s1 * c2 * s3;
                this._z = c1 * c2 * s3 + s1 * s2 * c3;
                this._w = c1 * c2 * c3 - s1 * s2 * s3;
                break;

            case "YXZ":
                this._x = s1 * c2 * c3 + c1 * s2 * s3;
                this._y = c1 * s2 * c3 - s1 * c2 * s3;
                this._z = c1 * c2 * s3 - s1 * s2 * c3;
                this._w = c1 * c2 * c3 + s1 * s2 * s3;
                break;

            case "ZXY":
                this._x = s1 * c2 * c3 - c1 * s2 * s3;
                this._y = c1 * s2 * c3 + s1 * c2 * s3;
                this._z = c1 * c2 * s3 + s1 * s2 * c3;
                this._w = c1 * c2 * c3 - s1 * s2 * s3;
                break;

            case "ZYX":
                this._x = s1 * c2 * c3 - c1 * s2 * s3;
                this._y = c1 * s2 * c3 + s1 * c2 * s3;
                this._z = c1 * c2 * s3 - s1 * s2 * c3;
                this._w = c1 * c2 * c3 + s1 * s2 * s3;
                break;

            case "YZX":
                this._x = s1 * c2 * c3 + c1 * s2 * s3;
                this._y = c1 * s2 * c3 + s1 * c2 * s3;
                this._z = c1 * c2 * s3 - s1 * s2 * c3;
                this._w = c1 * c2 * c3 - s1 * s2 * s3;
                break;

            case "XZY":
                this._x = s1 * c2 * c3 - c1 * s2 * s3;
                this._y = c1 * s2 * c3 - s1 * c2 * s3;
                this._z = c1 * c2 * s3 + s1 * s2 * c3;
                this._w = c1 * c2 * c3 + s1 * s2 * s3;
                break;

            default:
                JsConsole.warn("Quaternion: .setFromEuler() encountered an unknown order: " + order);
                break;
        }

        if (update) this._onChangeCallback();

        return this;
    }

    /// <summary>Assumes the upper 3×3 of m is a pure rotation matrix (i.e, unscaled).</summary>
    public ThreeQuaternion setFromRotationMatrix(ThreeMatrix4 m)
    {
        // http://www.euclideanspace.com/maths/geometry/rotations/conversions/matrixToQuaternion/index.htm
        double[] te = m.elements;

        double m11 = te[0], m12 = te[4], m13 = te[8],
            m21 = te[1], m22 = te[5], m23 = te[9],
            m31 = te[2], m32 = te[6], m33 = te[10],

            trace = m11 + m22 + m33;

        if (trace > 0)
        {
            double s = 0.5 / Math.sqrt(trace + 1.0);

            this._w = 0.25 / s;
            this._x = (m32 - m23) * s;
            this._y = (m13 - m31) * s;
            this._z = (m21 - m12) * s;
        }
        else if (m11 > m22 && m11 > m33)
        {
            double s = 2.0 * Math.sqrt(1.0 + m11 - m22 - m33);

            this._w = (m32 - m23) / s;
            this._x = 0.25 * s;
            this._y = (m12 + m21) / s;
            this._z = (m13 + m31) / s;
        }
        else if (m22 > m33)
        {
            double s = 2.0 * Math.sqrt(1.0 + m22 - m11 - m33);

            this._w = (m13 - m31) / s;
            this._x = (m12 + m21) / s;
            this._y = 0.25 * s;
            this._z = (m23 + m32) / s;
        }
        else
        {
            double s = 2.0 * Math.sqrt(1.0 + m33 - m11 - m22);

            this._w = (m21 - m12) / s;
            this._x = (m13 + m31) / s;
            this._y = (m23 + m32) / s;
            this._z = 0.25 * s;
        }

        this._onChangeCallback();

        return this;
    }

    /// <summary>`invert()` — the conjugate (quaternion is assumed to have unit length).</summary>
    public ThreeQuaternion invert()
    {
        this._x *= -1;
        this._y *= -1;
        this._z *= -1;

        this._onChangeCallback();

        return this;
    }

    public ThreeQuaternion premultiply(ThreeQuaternion q) => this.multiplyQuaternions(q, this);

    public ThreeQuaternion multiplyQuaternions(ThreeQuaternion a, ThreeQuaternion b)
    {
        // from http://www.euclideanspace.com/maths/algebra/realNormedAlgebra/quaternions/code/index.htm
        double qax = a._x, qay = a._y, qaz = a._z, qaw = a._w;
        double qbx = b._x, qby = b._y, qbz = b._z, qbw = b._w;

        this._x = qax * qbw + qaw * qbx + qay * qbz - qaz * qby;
        this._y = qay * qbw + qaw * qby + qaz * qbx - qax * qbz;
        this._z = qaz * qbw + qaw * qbz + qax * qby - qay * qbx;
        this._w = qaw * qbw - qax * qbx - qay * qby - qaz * qbz;

        this._onChangeCallback();

        return this;
    }
}

/// <summary>three.js `Euler` (angles in radians, default order 'XYZ').</summary>
public sealed class ThreeEuler
{
    public const string DEFAULT_ORDER = "XYZ";

    public double _x;
    public double _y;
    public double _z;
    public string _order;

    internal Action _onChangeCallback = () => { };

    public ThreeEuler(double x = 0, double y = 0, double z = 0, string order = DEFAULT_ORDER)
    {
        this._x = x;
        this._y = y;
        this._z = z;
        this._order = order;
    }

    /// <summary>`set(x, y, z, order = this._order)`.</summary>
    public ThreeEuler set(double x, double y, double z, string? order = null)
    {
        this._x = x;
        this._y = y;
        this._z = z;
        this._order = order ?? this._order;

        this._onChangeCallback();

        return this;
    }
}

/// <summary>A GPU texture the uniforms reference by identity. `role` tells the Godot layer which real texture to bind.</summary>
public sealed class ThreeTexture
{
    /// <summary>The three `DataTexture` returned by `getDFGLUT()` (`DFGLUTData.js`): 16×16 RG, name "DFG_LUT".</summary>
    public const string RoleDfgLut = "dfg-lut";
    /// <summary>The terrain sun's shadow-map depth texture (`WebGLShadowMap`: `light.name + '.shadowMap'`).</summary>
    public const string RoleTerrainSunShadowDepth = "terrain-sun-shadow-depth";

    public string name;
    public string role;
    public int width;
    public int height;

    public ThreeTexture(string name, string role, int width, int height)
    {
        this.name = name;
        this.role = role;
        this.width = width;
        this.height = height;
    }

    /// <summary>three `getDFGLUT()` caches one DataTexture for the whole renderer.</summary>
    public static readonly ThreeTexture DFG_LUT = new ThreeTexture("DFG_LUT", RoleDfgLut, 16, 16);
}

/// <summary>The part of three's `WebGLRenderTarget` a light shadow owns (`shadow.map`): colour and depth textures.</summary>
public sealed class ThreeShadowMapTarget
{
    public readonly ThreeTexture texture;
    public ThreeTexture? depthTexture;
    public readonly int width;
    public readonly int height;

    public ThreeShadowMapTarget(int width, int height, ThreeTexture texture)
    {
        this.width = width;
        this.height = height;
        this.texture = texture;
    }
}

/// <summary>three.js `Layers` (a 32-bit membership mask; objects start on layer 0).</summary>
public sealed class ThreeLayers
{
    /// <summary>three keeps `mask` as `>>> 0` (uint); only its bit pattern is ever tested, so a C# int is equivalent.</summary>
    public int mask = 1;

    public void set(int layer) => this.mask = 1 << layer;

    public void enable(int layer) => this.mask |= 1 << layer;

    public bool test(ThreeLayers layers) => (this.mask & layers.mask) != 0;
}

/// <summary>three.js `Object3D` (transform hierarchy only).</summary>
public class ThreeObject3D
{
    /// <summary>`Object3D.DEFAULT_UP` (a mutable static in three that the client never changes).</summary>
    public static readonly ThreeVector3 DEFAULT_UP = new ThreeVector3(0, 1, 0);
    public const bool DEFAULT_MATRIX_AUTO_UPDATE = true;
    public const bool DEFAULT_MATRIX_WORLD_AUTO_UPDATE = true;

    // three's module-level lookAt scratch (`_target`, `_position`, `_m1`, `_q1`). Main-thread-only.
    private static readonly ThreeVector3 _target = new ThreeVector3();
    private static readonly ThreeVector3 _position = new ThreeVector3();
    private static readonly ThreeMatrix4 _m1 = new ThreeMatrix4();
    private static readonly ThreeQuaternion _q1 = new ThreeQuaternion();

    public string name = "";

    public ThreeObject3D? parent;
    public readonly List<ThreeObject3D> children = new List<ThreeObject3D>();

    public readonly ThreeVector3 up = DEFAULT_UP.clone();

    public readonly ThreeVector3 position = new ThreeVector3();
    public readonly ThreeEuler rotation = new ThreeEuler();
    public readonly ThreeQuaternion quaternion = new ThreeQuaternion();
    public readonly ThreeVector3 scale = new ThreeVector3(1, 1, 1);

    public readonly ThreeMatrix4 matrix = new ThreeMatrix4();
    public readonly ThreeMatrix4 matrixWorld = new ThreeMatrix4();

    /// <summary>r185 rotation/scale pivot (null = none).</summary>
    public ThreeVector3? pivot = null;

    public bool matrixAutoUpdate = DEFAULT_MATRIX_AUTO_UPDATE;
    public bool matrixWorldAutoUpdate = DEFAULT_MATRIX_WORLD_AUTO_UPDATE;
    public bool matrixWorldNeedsUpdate = false;

    public readonly ThreeLayers layers = new ThreeLayers();
    public bool visible = true;
    public bool castShadow = false;
    public bool receiveShadow = false;
    public bool frustumCulled = true;
    public int renderOrder = 0;

    public virtual bool isCamera => false;
    public virtual bool isLight => false;

    public ThreeObject3D()
    {
        // `onRotationChange`: Euler writes drive the quaternion (see the file header for the omitted reverse direction).
        this.rotation._onChangeCallback = () => this.quaternion.setFromEuler(this.rotation, false);
    }

    public ThreeObject3D add(params ThreeObject3D[] objects)
    {
        foreach (ThreeObject3D @object in objects)
        {
            if (@object == this)
            {
                JsConsole.error("Object3D.add: object can't be added as a child of itself.");
                continue;
            }

            @object.removeFromParent();
            @object.parent = this;
            this.children.Add(@object);
        }

        return this;
    }

    public ThreeObject3D remove(params ThreeObject3D[] objects)
    {
        foreach (ThreeObject3D @object in objects)
        {
            int index = this.children.IndexOf(@object);

            if (index != -1)
            {
                @object.parent = null;
                this.children.RemoveAt(index);
            }
        }

        return this;
    }

    public ThreeObject3D removeFromParent()
    {
        ThreeObject3D? parent = this.parent;

        if (parent != null)
        {
            parent.remove(this);
        }

        return this;
    }

    public void lookAt(ThreeVector3 target) => this.lookAt(target.x, target.y, target.z);

    /// <summary>This method does not support objects having non-uniformly-scaled parent(s).</summary>
    public void lookAt(double x, double y, double z)
    {
        _target.set(x, y, z);

        ThreeObject3D? parent = this.parent;

        this.updateWorldMatrix(true, false);

        _position.setFromMatrixPosition(this.matrixWorld);

        if (this.isCamera || this.isLight)
        {
            _m1.lookAt(_position, _target, this.up);
        }
        else
        {
            _m1.lookAt(_target, _position, this.up);
        }

        this.quaternion.setFromRotationMatrix(_m1);

        if (parent != null)
        {
            _m1.extractRotation(parent.matrixWorld);
            _q1.setFromRotationMatrix(_m1);
            this.quaternion.premultiply(_q1.invert());
        }
    }

    public void traverse(Action<ThreeObject3D> callback)
    {
        callback(this);

        List<ThreeObject3D> children = this.children;

        for (int i = 0, l = children.Count; i < l; i++)
        {
            children[i].traverse(callback);
        }
    }

    public void updateMatrix()
    {
        this.matrix.compose(this.position, this.quaternion, this.scale);

        ThreeVector3? pivot = this.pivot;

        if (pivot != null)
        {
            double px = pivot.x, py = pivot.y, pz = pivot.z;
            double[] te = this.matrix.elements;

            te[12] += px - te[0] * px - te[4] * py - te[8] * pz;
            te[13] += py - te[1] * px - te[5] * py - te[9] * pz;
            te[14] += pz - te[2] * px - te[6] * py - te[10] * pz;
        }

        this.matrixWorldNeedsUpdate = true;
    }

    public virtual void updateMatrixWorld(bool force = false)
    {
        if (this.matrixAutoUpdate) this.updateMatrix();

        if (this.matrixWorldNeedsUpdate || force)
        {
            if (this.matrixWorldAutoUpdate == true)
            {
                if (this.parent == null)
                {
                    this.matrixWorld.copy(this.matrix);
                }
                else
                {
                    this.matrixWorld.multiplyMatrices(this.parent.matrixWorld, this.matrix);
                }
            }

            this.matrixWorldNeedsUpdate = false;

            force = true;
        }

        // make sure descendants are updated if required
        List<ThreeObject3D> children = this.children;

        for (int i = 0, l = children.Count; i < l; i++)
        {
            ThreeObject3D child = children[i];

            child.updateMatrixWorld(force);
        }
    }

    public virtual void updateWorldMatrix(bool updateParents, bool updateChildren, bool force = false)
    {
        ThreeObject3D? parent = this.parent;

        if (updateParents == true && parent != null)
        {
            parent.updateWorldMatrix(true, false);
        }

        if (this.matrixAutoUpdate) this.updateMatrix();

        if (this.matrixWorldNeedsUpdate || force)
        {
            if (this.matrixWorldAutoUpdate == true)
            {
                if (this.parent == null)
                {
                    this.matrixWorld.copy(this.matrix);
                }
                else
                {
                    this.matrixWorld.multiplyMatrices(this.parent.matrixWorld, this.matrix);
                }
            }

            this.matrixWorldNeedsUpdate = false;

            force = true;
        }

        // make sure descendants are updated
        if (updateChildren == true)
        {
            List<ThreeObject3D> children = this.children;

            for (int i = 0, l = children.Count; i < l; i++)
            {
                ThreeObject3D child = children[i];

                child.updateWorldMatrix(false, true, force);
            }
        }
    }
}

/// <summary>three.js `Group`.</summary>
public class ThreeGroup : ThreeObject3D
{
}

/// <summary>three.js `Scene` (no background/environment/fog: the terrain scene leaves all three null).</summary>
public sealed class ThreeScene : ThreeObject3D
{
}

/// <summary>three.js `Mesh` reduced to what the renderer emulation reads (material, depth twin, draw flags).</summary>
public sealed class ThreeMesh : ThreeObject3D
{
    public ThreeMaterial material;
    public ThreeMaterial? customDepthMaterial;

    public ThreeMesh(ThreeMaterial material)
    {
        this.material = material;
    }
}

/// <summary>three.js `Camera`.</summary>
public class ThreeCamera : ThreeObject3D
{
    // three's module scratch for the scale-free view matrix. Main-thread-only.
    private static readonly ThreeVector3 _position = new ThreeVector3();
    private static readonly ThreeQuaternion _quaternion = new ThreeQuaternion();
    private static readonly ThreeVector3 _scale = new ThreeVector3();
    public override bool isCamera => true;

    public readonly ThreeMatrix4 matrixWorldInverse = new ThreeMatrix4();
    public readonly ThreeMatrix4 projectionMatrix = new ThreeMatrix4();
    public readonly ThreeMatrix4 projectionMatrixInverse = new ThreeMatrix4();
    public int coordinateSystem = ThreeConstants.WebGLCoordinateSystem;
    public bool _reversedDepth = false;

    public bool reversedDepth => this._reversedDepth;

    public override void updateMatrixWorld(bool force = false)
    {
        base.updateMatrixWorld(force);

        // exclude scale from view matrix to be glTF conform
        this.matrixWorld.decompose(_position, _quaternion, _scale);

        if (_scale.x == 1 && _scale.y == 1 && _scale.z == 1)
        {
            this.matrixWorldInverse.copy(this.matrixWorld).invert();
        }
        else
        {
            this.matrixWorldInverse.compose(_position, _quaternion, _scale.set(1, 1, 1)).invert();
        }
    }

    public override void updateWorldMatrix(bool updateParents, bool updateChildren, bool force = false)
    {
        base.updateWorldMatrix(updateParents, updateChildren, force);

        // exclude scale from view matrix to be glTF conform
        this.matrixWorld.decompose(_position, _quaternion, _scale);

        if (_scale.x == 1 && _scale.y == 1 && _scale.z == 1)
        {
            this.matrixWorldInverse.copy(this.matrixWorld).invert();
        }
        else
        {
            this.matrixWorldInverse.compose(_position, _quaternion, _scale.set(1, 1, 1)).invert();
        }
    }
}

/// <summary>three.js `OrthographicCamera` (no `view` offset: the terrain never sets one).</summary>
public sealed class ThreeOrthographicCamera : ThreeCamera
{
    public double zoom = 1;
    public double left;
    public double right;
    public double top;
    public double bottom;
    public double near;
    public double far;

    public ThreeOrthographicCamera(double left = -1, double right = 1, double top = 1, double bottom = -1, double near = 0.1, double far = 2000)
    {
        this.left = left;
        this.right = right;
        this.top = top;
        this.bottom = bottom;
        this.near = near;
        this.far = far;

        this.updateProjectionMatrix();
    }

    public void updateProjectionMatrix()
    {
        double dx = (this.right - this.left) / (2 * this.zoom);
        double dy = (this.top - this.bottom) / (2 * this.zoom);
        double cx = (this.right + this.left) / 2;
        double cy = (this.top + this.bottom) / 2;

        double left = cx - dx;
        double right = cx + dx;
        double top = cy + dy;
        double bottom = cy - dy;

        this.projectionMatrix.makeOrthographic(left, right, top, bottom, this.near, this.far, this.coordinateSystem, this.reversedDepth);

        this.projectionMatrixInverse.copy(this.projectionMatrix).invert();
    }
}

/// <summary>three.js `PerspectiveCamera` (only as the idle point-light shadow camera; no view offset, no film offset).</summary>
public sealed class ThreePerspectiveCamera : ThreeCamera
{
    public double fov;
    public double zoom = 1;
    public double near;
    public double far;
    public double aspect;

    public ThreePerspectiveCamera(double fov = 50, double aspect = 1, double near = 0.1, double far = 2000)
    {
        this.fov = fov;
        this.near = near;
        this.far = far;
        this.aspect = aspect;

        this.updateProjectionMatrix();
    }

    public void updateProjectionMatrix()
    {
        double near = this.near;
        double top = near * Math.tan(ThreeConstants.DEG2RAD * 0.5 * this.fov) / this.zoom;
        double height = 2 * top;
        double width = this.aspect * height;
        double left = -0.5 * width;

        // `filmOffset` is 0 for every terrain camera (the skew branch never runs).

        this.projectionMatrix.makePerspective(left, left + width, top, top - height, near, this.far, this.coordinateSystem, this.reversedDepth);

        this.projectionMatrixInverse.copy(this.projectionMatrix).invert();
    }
}

/// <summary>three.js `LightShadow`.</summary>
public class ThreeLightShadow
{
    // three's module scratch. Main-thread-only.
    private static readonly ThreeMatrix4 _projScreenMatrix = new ThreeMatrix4();
    private static readonly ThreeVector3 _lightPositionWorld = new ThreeVector3();
    private static readonly ThreeVector3 _lookTarget = new ThreeVector3();

    public ThreeCamera camera;
    public double intensity = 1;
    public double bias = 0;
    public double normalBias = 0;
    public double radius = 1;
    public readonly ThreeVector2 mapSize = new ThreeVector2(512, 512);
    public ThreeShadowMapTarget? map = null;
    public readonly ThreeMatrix4 matrix = new ThreeMatrix4();
    public bool autoUpdate = true;
    public bool needsUpdate = false;

    private readonly ThreeVector2 _frameExtents = new ThreeVector2(1, 1);

    public ThreeLightShadow(ThreeCamera camera)
    {
        this.camera = camera;
    }

    /// <summary>
    /// `updateMatrices(light)`. The frustum three derives here (`_frustum.setFromProjectionMatrix`) only drives its
    /// shadow-pass culling and is not ported.
    /// </summary>
    public virtual void updateMatrices(ThreeDirectionalLight light)
    {
        ThreeCamera shadowCamera = this.camera;
        ThreeMatrix4 shadowMatrix = this.matrix;

        _lightPositionWorld.setFromMatrixPosition(light.matrixWorld);
        shadowCamera.position.copy(_lightPositionWorld);

        _lookTarget.setFromMatrixPosition(light.target.matrixWorld);
        shadowCamera.lookAt(_lookTarget);
        shadowCamera.updateMatrixWorld();

        _projScreenMatrix.multiplyMatrices(shadowCamera.projectionMatrix, shadowCamera.matrixWorldInverse);

        if (shadowCamera.coordinateSystem == ThreeConstants.WebGPUCoordinateSystem || shadowCamera.reversedDepth)
        {
            shadowMatrix.set(
                0.5, 0.0, 0.0, 0.5,
                0.0, 0.5, 0.0, 0.5,
                0.0, 0.0, 1.0, 0.0, // Identity Z (preserving the correct [0, 1] range from the projection matrix)
                0.0, 0.0, 0.0, 1.0);
        }
        else
        {
            shadowMatrix.set(
                0.5, 0.0, 0.0, 0.5,
                0.0, 0.5, 0.0, 0.5,
                0.0, 0.0, 0.5, 0.5,
                0.0, 0.0, 0.0, 1.0);
        }

        shadowMatrix.multiply(_projScreenMatrix);
    }

    public ThreeVector2 getFrameExtents() => this._frameExtents;
}

/// <summary>three.js `DirectionalLightShadow`.</summary>
public sealed class ThreeDirectionalLightShadow : ThreeLightShadow
{
    public ThreeDirectionalLightShadow() : base(new ThreeOrthographicCamera(-5, 5, 5, -5, 0.5, 500)) { }
}

/// <summary>three.js `PointLightShadow` (never rendered: the terrain's impact lights never cast).</summary>
public sealed class ThreePointLightShadow : ThreeLightShadow
{
    public ThreePointLightShadow() : base(new ThreePerspectiveCamera(90, 1, 0.5, 500)) { }
}

/// <summary>three.js `Light`. `shadow` is a plain field so two lights can share one LightShadow (three allows it).</summary>
public abstract class ThreeLight : ThreeObject3D
{
    public override bool isLight => true;
    public readonly ThreeColor color;
    public double intensity;

    /// <summary>`light.shadow` (undefined → null on ambient/hemisphere lights).</summary>
    public ThreeLightShadow? shadow;

    protected ThreeLight(double color, double intensity = 1)
    {
        this.color = new ThreeColor(color);
        this.intensity = intensity;
    }

    /// <summary>`light.map` (only spot lights carry one; always null for the terrain's light types).</summary>
    public virtual ThreeTexture? map => null;
}

/// <summary>three.js `AmbientLight`.</summary>
public sealed class ThreeAmbientLight : ThreeLight
{
    public ThreeAmbientLight(double color, double intensity) : base(color, intensity) { }
}

/// <summary>three.js `HemisphereLight`.</summary>
public sealed class ThreeHemisphereLight : ThreeLight
{
    public readonly ThreeColor groundColor;

    public ThreeHemisphereLight(double skyColor, double groundColor, double intensity) : base(skyColor, intensity)
    {
        this.position.copy(ThreeObject3D.DEFAULT_UP);
        this.updateMatrix();

        this.groundColor = new ThreeColor(groundColor);
    }
}

/// <summary>three.js `DirectionalLight`.</summary>
public sealed class ThreeDirectionalLight : ThreeLight
{
    public ThreeObject3D target = new ThreeObject3D();

    public ThreeDirectionalLight(double color, double intensity) : base(color, intensity)
    {
        this.position.copy(ThreeObject3D.DEFAULT_UP);
        this.updateMatrix();

        this.shadow = new ThreeDirectionalLightShadow();
    }
}

/// <summary>three.js `PointLight`.</summary>
public sealed class ThreePointLight : ThreeLight
{
    public double distance;
    public double decay;

    public ThreePointLight(double color, double intensity, double distance = 0, double decay = 2) : base(color, intensity)
    {
        this.distance = distance;
        this.decay = decay;

        this.shadow = new ThreePointLightShadow();
    }
}
