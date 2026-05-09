use std::ops::{Add, AddAssign, Div, Mul, Neg, Sub, SubAssign};

#[derive(Copy, Clone, Debug, PartialEq)]
pub struct Vec3d {
    pub x: f64,
    pub y: f64,
    pub z: f64,
}

impl Vec3d {
    pub const ZERO: Vec3d = Vec3d { x: 0.0, y: 0.0, z: 0.0 };

    pub const fn new(x: f64, y: f64, z: f64) -> Self {
        Vec3d { x, y, z }
    }

    pub fn norm_squared(self) -> f64 {
        self.x * self.x + self.y * self.y + self.z * self.z
    }

    pub fn norm(self) -> f64 {
        self.norm_squared().sqrt()
    }

    pub fn normalize(self) -> Vec3d {
        let n = self.norm();
        if n == 0.0 { Vec3d::ZERO } else { self / n }
    }

    pub fn dot(self, other: Vec3d) -> f64 {
        self.x * other.x + self.y * other.y + self.z * other.z
    }

    pub fn cross(self, other: Vec3d) -> Vec3d {
        Vec3d::new(
            self.y * other.z - self.z * other.y,
            self.z * other.x - self.x * other.z,
            self.x * other.y - self.y * other.x,
        )
    }
}

impl Add for Vec3d {
    type Output = Vec3d;
    fn add(self, rhs: Vec3d) -> Vec3d {
        Vec3d::new(self.x + rhs.x, self.y + rhs.y, self.z + rhs.z)
    }
}

impl AddAssign for Vec3d {
    fn add_assign(&mut self, rhs: Vec3d) { *self = *self + rhs; }
}

impl Sub for Vec3d {
    type Output = Vec3d;
    fn sub(self, rhs: Vec3d) -> Vec3d {
        Vec3d::new(self.x - rhs.x, self.y - rhs.y, self.z - rhs.z)
    }
}

impl SubAssign for Vec3d {
    fn sub_assign(&mut self, rhs: Vec3d) { *self = *self - rhs; }
}

impl Mul<f64> for Vec3d {
    type Output = Vec3d;
    fn mul(self, rhs: f64) -> Vec3d { Vec3d::new(self.x * rhs, self.y * rhs, self.z * rhs) }
}

impl Mul<Vec3d> for f64 {
    type Output = Vec3d;
    fn mul(self, rhs: Vec3d) -> Vec3d { rhs * self }
}

impl Div<f64> for Vec3d {
    type Output = Vec3d;
    fn div(self, rhs: f64) -> Vec3d { Vec3d::new(self.x / rhs, self.y / rhs, self.z / rhs) }
}

impl Neg for Vec3d {
    type Output = Vec3d;
    fn neg(self) -> Vec3d { Vec3d::new(-self.x, -self.y, -self.z) }
}
