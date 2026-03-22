class User < ApplicationRecord
  has_many :posts
  belongs_to :account
  has_one :profile

  validates :email, presence: true, uniqueness: true
  before_action :normalize_email, only: [:create, :update]
  after_action :audit_changes
end