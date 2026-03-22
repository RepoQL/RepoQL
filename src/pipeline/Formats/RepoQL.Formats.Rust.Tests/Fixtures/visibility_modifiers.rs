pub struct PublicType;
pub(crate) struct CrateType;

mod outer {
    pub(super) struct SuperType;
    pub(in crate::outer) struct PathType;
    struct PrivateType;

    pub(crate) fn crate_visible() {
    }

    fn private_fn() {
    }
}