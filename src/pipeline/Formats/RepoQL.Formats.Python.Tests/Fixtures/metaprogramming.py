import importlib


def __getattr__(name: str):
    """Module-level __getattr__ (PEP 562)."""
    raise AttributeError(name)


def __dir__():
    """Module-level __dir__ (PEP 562)."""
    return ["Dynamic", "make_dynamic"]


class Dynamic:
    def __getattr__(self, name: str):
        return name


def make_dynamic():
    exec("x = 1")
    eval("1 + 1")
    kind = type("Temp", (), {})
    setattr(kind, "name", "value")
    __import__("math")
    importlib.import_module("json")
    return kind
