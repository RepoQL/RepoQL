from contextlib import asynccontextmanager


@asynccontextmanager
async def manager():
    yield "resource"


async def stream(values: list[int]):
    async with manager() as item:
        async for value in values:
            yield item, value


async def regular() -> int:
    return 1
