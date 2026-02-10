namespace RepoQL.Formats.Ruby.TreeSitter;

internal static class RubyQueries
{
    public const string ClassDeclarations = """
        ;; Classes (with superclass detection for reopening heuristic)
        (class name: (constant) @class_name
               superclass: (superclass (scope_resolution)? @super)?)
        """;

    public const string ModuleDeclarations = """
        ;; Modules
        (module name: (constant) @module_name)
        """;

    public const string MethodDeclarations = """
        ;; Methods (with parameter details)
        (method name: (identifier) @method_name
                parameters: (method_parameters)? @params) @method_node
        """;

    public const string SingletonMethods = """
        ;; Singleton methods (class methods, or methods on specific objects)
        (singleton_method
            object: (_) @receiver
            name: (identifier) @method_name
            parameters: (method_parameters)? @params) @singleton_method
        """;

    public const string SingletonClassBlocks = """
        ;; Singleton class blocks (class << self)
        (singleton_class value: (_) @target) @singleton_class
        """;

    public const string AttributeAccessors = """
        ;; Attribute accessors
        (call method: (identifier) @call_name
              arguments: (argument_list) @args
         (#match? @call_name "^attr_(reader|writer|accessor)$")) @attribute_call
        """;

    public const string Mixins = """
        ;; Include/extend/prepend (with ordinal from source position)
        (call method: (identifier) @mixin_type
              arguments: (argument_list
                [
                  (constant)
                  (self)
                ] @module)
         (#match? @mixin_type "^(include|extend|prepend)$")) @mixin_call
        """;

    public const string Constants = """
        ;; Constants
        (assignment left: (constant) @const_name) @const_assignment
        """;

    public const string RequireStatements = """
        ;; Require statements
        (call method: (identifier) @req_method
              arguments: (argument_list (string (string_content) @path))
         (#match? @req_method "^require(_relative)?$")) @require_call
        """;

    public const string YieldSites = """
        ;; Yield detection (within method bodies — indicates block acceptance)
        (yield) @yield_site
        """;

    public const string BlockParameters = """
        ;; Block parameters
        (block_parameter (identifier) @block_param)
        """;

    public const string MetaprogrammingCalls = """
        ;; Metaprogramming with dynamic names (for honesty annotations)
        (call method: (identifier) @meta_method
              arguments: (argument_list) @args
         (#match? @meta_method "^(define_method|class_eval|module_eval|instance_eval)$")) @meta_call
        (call method: (identifier) @meta_method
              !arguments
         (#match? @meta_method "^(define_method|class_eval|module_eval|instance_eval)$")) @meta_call
        """;

    public const string MethodMissingDefinitions = """
        ;; method_missing definition (dynamic dispatch hint)
        (method name: (identifier) @method_name
         (#eq? @method_name "method_missing")) @method_missing_def
        """;

    public const string DelegateCalls = """
        ;; Rails delegate
        (call method: (identifier) @method
              arguments: (argument_list) @args
         (#eq? @method "delegate")) @delegate_call
        """;

    public const string ScopeCalls = """
        ;; Rails scope
        (call method: (identifier) @method
              arguments: (argument_list) @args
         (#eq? @method "scope")) @scope_call
        """;

    public const string AssociationCalls = """
        ;; Rails associations
        (call method: (identifier) @method
              arguments: (argument_list) @args
         (#match? @method "^(has_many|belongs_to|has_one)$")) @association_call
        """;

    public const string ValidationCalls = """
        ;; Rails validations
        (call method: (identifier) @method
              arguments: (argument_list) @args
         (#eq? @method "validates")) @validation_call
        """;

    public const string CallbackCalls = """
        ;; Rails callbacks
        (call method: (identifier) @method
              arguments: (argument_list) @args
         (#match? @method "^(before_action|after_action)$")) @callback_call
        """;

    public const string DefineMethodCalls = """
        ;; define_method calls
        (call method: (identifier) @method
              arguments: (argument_list) @args
         (#eq? @method "define_method")) @define_method_call
        """;

    public const string VisibilityBare = """
        ;; Bare visibility modifier (scope change)
        (body_statement (identifier) @vis)
        """;

    public const string VisibilityTargeted = """
        ;; Method-level visibility modifier
        (call method: (identifier) @vis
              arguments: (argument_list (simple_symbol) @target)
         (#match? @vis "^(public|private|protected)$")) @visibility_call
        """;

    public const string AliasStatements = """
        ;; Alias statements
        (alias name: (_) @new_name alias: (_) @original_name) @alias_node
        """;

    public const string AliasMethodCalls = """
        ;; alias_method calls
        (call method: (identifier) @call_name
              arguments: (argument_list (_) @new_name (_) @original_name)
         (#eq? @call_name "alias_method")) @alias_call
        """;
}
