PREAMBLE_MARKER = "preamble-marker"

def shallow_utility
  1
end

class EventDispatcher
  def initialize
    @handlers = []
  end

  def on(handler)
    @handlers.push(handler)
  end

  def emit(payload)
    @handlers.each do |h|
      h.call(payload)
    end
  end
end

def create_event(name, value)
  dispatcher = EventDispatcher.new
  dispatcher.on(->(p) { puts p.to_s })
  normalized = name.strip.downcase
  scaled = value * 1000
  { name: normalized, value: scaled }
end

def deep_target_function(items)
  total = 0
  items.each do |item|
    if item > 0
      total += item * 2
    else
      total -= 1
    end
  end
  total
end
