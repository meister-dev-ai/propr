; Definition-capture query for the tree-sitter-go grammar, vendored against
; TreeSitter.DotNet 1.3.0. Derived from upstream tree-sitter-go queries/tags.scm.
; Doc-comment predicates are dropped - the prefetch path needs only the definition
; node and its name.

(function_declaration
  name: (identifier) @name) @definition.function

(method_declaration
  name: (field_identifier) @name) @definition.method

(type_spec
  name: (type_identifier) @name) @definition.type
