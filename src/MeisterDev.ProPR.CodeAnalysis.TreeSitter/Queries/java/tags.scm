; Definition-capture query for the tree-sitter-java grammar, vendored against
; TreeSitter.DotNet 1.3.0. Derived from upstream tree-sitter-java queries/tags.scm.
; Reference captures are dropped.

(class_declaration
  name: (identifier) @name) @definition.class

(interface_declaration
  name: (identifier) @name) @definition.interface

(enum_declaration
  name: (identifier) @name) @definition.enum

(record_declaration
  name: (identifier) @name) @definition.class

(annotation_type_declaration
  name: (identifier) @name) @definition.annotation

(method_declaration
  name: (identifier) @name) @definition.method

(constructor_declaration
  name: (identifier) @name) @definition.method
