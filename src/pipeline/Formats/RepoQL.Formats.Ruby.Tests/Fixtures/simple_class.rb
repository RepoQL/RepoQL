module App
  class User < BaseUser
    include Trackable
    extend ClassMethods
    prepend Auditable

    attr_accessor :name, :email
    CONSTANT = 123

    private
    def secret(token)
      yield token
    end

    public
    def greet(name, &block)
      block.call(name)
    end

    def self.build(id)
      new
    end

    class << self
      def from_json(json)
        new
      end
    end

    alias username name
    alias_method :display_name, :greet
    define_method(:dynamic_name) { |value| value }
  end
end

require "json"

def top_level(arg)
  arg
end
