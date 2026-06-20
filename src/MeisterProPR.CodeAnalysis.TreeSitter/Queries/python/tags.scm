; Definition-capture query for the tree-sitter-python grammar, vendored against
; TreeSitter.DotNet 1.3.0. Derived from upstream tree-sitter-python queries/tags.scm.

(class_definition
  name: (identifier) @name) @definition.class

(function_definition
  name: (identifier) @name) @definition.function
