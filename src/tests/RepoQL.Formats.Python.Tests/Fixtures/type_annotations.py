from typing import Optional


def typed(value: int, name: str = "a", *, flag: bool = False, **extras: str) -> Optional[str]:
    return name if flag else None


def separators(a, /, b: int, *, c: str, **kwargs: int) -> None:
    return None
