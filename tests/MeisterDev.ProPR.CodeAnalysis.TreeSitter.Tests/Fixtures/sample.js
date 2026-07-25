const preamble = "preamble-marker";

function shallowUtility() {
    return 1;
}

class EventDispatcher {
    constructor() {
        this.handlers = [];
    }

    on(handler) {
        this.handlers.push(handler);
    }

    emit(payload) {
        for (const h of this.handlers) {
            h(payload);
        }
    }
}

function createEvent(name, value) {
    const dispatcher = new EventDispatcher();
    dispatcher.on((p) => console.log(String(p)));
    const normalized = name.trim().toLowerCase();
    const scaled = value * 1000;
    return {
        name: normalized,
        value: scaled,
    };
}

function deepTargetFunction(items) {
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

module.exports = {
    shallowUtility,
    createEvent,
    deepTargetFunction,
    EventDispatcher,
};
