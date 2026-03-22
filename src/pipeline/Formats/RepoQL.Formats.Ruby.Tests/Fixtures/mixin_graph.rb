class Customer < ApplicationRecord
  include Alpha
  include Beta
  prepend Gatekeeper
  extend TypeMethods
end

module Formatting
  extend self
  include Printable
  prepend Auditable
end
