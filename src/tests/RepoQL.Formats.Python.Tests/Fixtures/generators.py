def numbers(limit: int):
    for i in range(limit):
        yield i


def combine(other):
    yield from other
