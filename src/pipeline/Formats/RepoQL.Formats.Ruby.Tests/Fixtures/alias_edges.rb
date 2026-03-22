class AliasExample
  def original
  end

  def copy
  end

  alias copy original
  alias_method :renamed, :copy
end
