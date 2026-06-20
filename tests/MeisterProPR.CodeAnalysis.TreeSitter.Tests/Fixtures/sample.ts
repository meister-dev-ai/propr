import { Logger } from "./logger";

const preamble = "preamble-marker";

export function shallowUtility(): number {
    return 1;
}

export interface Widget {
    id: number;
    label: string;
}

export class EventDispatcher {
    private handlers: ((payload: unknown) => void)[] = [];

    on(handler: (payload: unknown) => void): void {
        this.handlers.push(handler);
    }

    emit(payload: unknown): void {
        for (const h of this.handlers) {
            h(payload);
        }
    }
}

export function createEvent(name: string, value: number): { name: string; value: number } {
    const dispatcher = new EventDispatcher();
    dispatcher.on((p) => Logger.log(String(p)));
    const normalized = name.trim().toLowerCase();
    const scaled = value * 1000;
    return {
        name: normalized,
        value: scaled,
    };
}

export function deepTargetFunction(items: number[]): number {
    let total = 0;
    for (const item of items) {
        if (item > 0) {
            total += item * 2;
        } else {
            total -= 1;
        }
    }
    return total;
}
