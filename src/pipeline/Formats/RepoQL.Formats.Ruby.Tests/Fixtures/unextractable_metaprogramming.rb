class DynamicUser
  method_name = :runtime_name

  define_method(method_name) do |value|
    value
  end

  class_eval do
    def class_eval_defined
    end
  end

  module_eval do
    def module_eval_defined
    end
  end

  instance_eval do
    def instance_eval_defined
    end
  end

  def method_missing(name, *args)
    super
  end

  eval("puts 'hello'")
end