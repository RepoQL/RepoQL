from typing import Protocol


class Greeter(Protocol):
    def greet(self, name: str) -> str:
        ...
