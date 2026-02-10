class PaymentGateway
  def self.build(client)
    new(client)
  end

  def net_total(amount)
    amount
  end

  def client.currency(amount)
    amount
  end

  class << self
    def from_env(config)
      new(config)
    end
  end
end
