import logging
from typing import List, Optional

PREAMBLE = "preamble-marker"


def shallow_utility() -> int:
    return 1


class EventDispatcher:
    def __init__(self) -> None:
        self._handlers: list = []

    def on(self, handler) -> None:
        self._handlers.append(handler)

    def emit(self, payload) -> None:
        for h in self._handlers:
            h(payload)


def create_event(name: str, value: int) -> dict:
    dispatcher = EventDispatcher()
    dispatcher.on(lambda p: logging.info(str(p)))
    normalized = name.strip().lower()
    scaled = value * 1000
    return {
        "name": normalized,
        "value": scaled,
    }


def deep_target_function(items: List[int]) -> int:
    total = 0
    for item in items:
        if item > 0:
            total += item * 2
        else:
            total -= 1
    return total
