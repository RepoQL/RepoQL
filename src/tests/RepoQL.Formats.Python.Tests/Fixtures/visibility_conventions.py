class Visibility:
    def public(self):
        return 1

    def _private(self):
        return 2

    def __mangled(self):
        return 3

    def __dunder__(self):
        return 4
