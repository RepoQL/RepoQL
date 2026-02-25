#![allow(dead_code)]

/// A user in the system.
#[derive(Debug, Clone)]
#[repr(C)]
pub struct User<T>
where
    T: Clone,
{
    /// Public identifier.
    pub id: u64,
    /// Internal value.
    value: T,
}