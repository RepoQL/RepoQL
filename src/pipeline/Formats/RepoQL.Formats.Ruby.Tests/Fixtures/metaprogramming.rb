class Account
  private
  attr_reader :token

  attr_writer :password
  attr_accessor :name, :email

  delegate :profile_name, :profile_age, to: :profile
  scope :active, -> { where(active: true) }

  define_method(:display_name) { |prefix| "#{prefix} #{name}" }
  define_method('legacy_code') { 'v1' }
end