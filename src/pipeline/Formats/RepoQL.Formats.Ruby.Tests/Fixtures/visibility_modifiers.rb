class VisibilityExample
  def open_method
    true
  end

  private

  def private_method
    false
  end

  protected :open_method
end
