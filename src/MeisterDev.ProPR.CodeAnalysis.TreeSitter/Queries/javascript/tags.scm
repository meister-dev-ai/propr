; Definition-capture query for the tree-sitter-javascript grammar, vendored against
; TreeSitter.DotNet 1.3.0. Derived from upstream tree-sitter-javascript queries/tags.scm
; (v0.23.x) with the reference captures dropped. The doc-comment predicates are also
; dropped to keep the query minimal and language-tooling-agnostic; the prefetch path
; only needs the definition node and its name.

(method_definition
  name: (property_identifier) @name) @definition.method

(class_declaration
  name: (identifier) @name) @definition.class

(function_declaration
  name: (identifier) @name) @definition.function

(generator_function_declaration
  name: (identifier) @name) @definition.function

(lexical_declaration
  (variable_declarator
    name: (identifier) @name
    value: [(arrow_function) (function_expression)]) @definition.function)

(variable_declaration
  (variable_declarator
    name: (identifier) @name
    value: [(arrow_function) (function_expression)]) @definition.function)
