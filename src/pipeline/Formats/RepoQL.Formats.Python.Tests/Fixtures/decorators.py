from abc import abstractmethod
from typing import overload


def override(func):
    return func


class DecoratorTarget:
    @property
    def prop(self) -> int:
        return 42

    @staticmethod
    def static(a: int) -> int:
        return a

    @classmethod
    def from_value(cls, value: int) -> "DecoratorTarget":
        return cls()

    @abstractmethod
    def abstract(self) -> None:
        raise NotImplementedError


@overload
def pick(value: int) -> int:
    ...


@overload
def pick(value: str) -> str:
    ...


@override
@custom.decorator("x", enabled=True)
def pick(value):
    return value
