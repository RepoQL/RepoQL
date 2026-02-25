class Example:
    def __init__(self, name: str, count: int):
        self.name = name
        self.count: int = count
        self.cache = {}

    def update(self, value: str):
        self.other = value
