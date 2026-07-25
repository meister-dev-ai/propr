package fixtures

const PreambleMarker = "preamble-marker"

func shallowUtility() int {
	return 1
}

type EventDispatcher struct {
	handlers []func(payload interface{})
}

func (d *EventDispatcher) On(handler func(payload interface{})) {
	d.handlers = append(d.handlers, handler)
}

func (d *EventDispatcher) Emit(payload interface{}) {
	for _, h := range d.handlers {
		h(payload)
	}
}

func createEvent(name string, value int) (string, int) {
	dispatcher := EventDispatcher{}
	dispatcher.On(func(payload interface{}) {})
	normalized := name
	scaled := value * 1000
	return normalized, scaled
}

func deepTargetFunction(items []int) int {
	total := 0
	for _, item := range items {
		if item > 0 {
			total += item * 2
		} else {
			total -= 1
		}
	}
	return total
}
