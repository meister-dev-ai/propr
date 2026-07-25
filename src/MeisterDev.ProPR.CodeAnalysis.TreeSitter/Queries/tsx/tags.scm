; Definition-capture query for the tree-sitter-typescript grammar (TSX), vendored
; against TreeSitter.DotNet 1.3.0. Same shape as the TypeScript query - the TSX
; grammar shares the TypeScript grammar's definition node-types.

(function_declaration
  name: (identifier) @name) @definition.function

(function_signature
  name: (identifier) @name) @definition.function

(generator_function_declaration
  name: (identifier) @name) @definition.function

(method_definition
  name: (property_identifier) @name) @definition.method

(method_signature
  name: (property_identifier) @name) @definition.method

(abstract_method_signature
  name: (property_identifier) @name) @definition.method

(class_declaration
  name: (type_identifier) @name) @definition.class

(abstract_class_declaration
  name: (type_identifier) @name) @definition.class

(interface_declaration
  name: (type_identifier) @name) @definition.interface

(enum_declaration
  name: (identifier) @name) @definition.enum

(internal_module
  name: (identifier) @name) @definition.module

(module
  name: (identifier) @name) @definition.module

(type_alias_declaration
  name: (type_identifier) @name) @definition.type
