; Definition-capture query for the tree-sitter-ruby grammar, vendored against
; TreeSitter.DotNet 1.3.0. Derived from upstream tree-sitter-ruby queries/tags.scm.
; Doc-comment predicates and reference captures are dropped.

(method
  name: (_) @name) @definition.method

(singleton_method
  name: (_) @name) @definition.method

(class
  name: [
    (constant) @name
    (scope_resolution
      name: (_) @name)
  ]) @definition.class

(singleton_class
  value: [
    (constant) @name
    (scope_resolution
      name: (_) @name)
  ]) @definition.class

(module
  name: [
    (constant) @name
    (scope_resolution
      name: (_) @name)
  ]) @definition.module
