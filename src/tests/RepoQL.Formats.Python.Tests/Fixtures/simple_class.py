import os
from typing import TYPE_CHECKING, Final

if TYPE_CHECKING:
    from app.types import UserPayload


def helper(value: str) -> str:
    return value


@decorators.model("user")
class User(BaseUser, Trackable, metaclass=ABCMeta):
    """Simple user class."""

    __slots__ = ("name", "email")
    KIND: Final[str] = "user"
    level: int = 1

    @staticmethod
    def build(name: str, email: str) -> "User":
        """Build a user instance."""
        return User(name, email)

    def __init__(self, name: str, email: str, *, active: bool = True):
        """Initialize values."""
        self.name = name
        self.email: str = email
        self.active = active

    def greet(self, who: str) -> str:
        return f"Hello {who}"
