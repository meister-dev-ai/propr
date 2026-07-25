// NOSONAR — this file is a tree-sitter test fixture, not a Java source file consumed by the build. Package name intentionally does not match the fixture folder casing so the analyzer tests can reference it from a stable path.
package fixtures;

import java.util.ArrayList;
import java.util.List;

public class Sample {

    private static final String PREAMBLE_MARKER = "preamble-marker";

    public static int shallowUtility() {
        return 1;
    }

    public static class EventDispatcher {
        private final List<java.util.function.Consumer<Object>> handlers = new ArrayList<>();

        public void on(java.util.function.Consumer<Object> handler) {
            handlers.add(handler);
        }

        public void emit(Object payload) {
            for (java.util.function.Consumer<Object> h : handlers) {
                h.accept(payload);
            }
        }
    }

    public static Event createEvent(String name, int value) {
        EventDispatcher dispatcher = new EventDispatcher();
        dispatcher.on(p -> System.out.println(String.valueOf(p)));
        String normalized = name.trim().toLowerCase();
        int scaled = value * 1000;
        return new Event(normalized, scaled);
    }

    public static int deepTargetFunction(List<Integer> items) {
        int total = 0;
        for (int item : items) {
            if (item > 0) {
                total += item * 2;
            } else {
                total -= 1;
            }
        }
        return total;
    }

    public record Event(String name, int value) {
    }
}
