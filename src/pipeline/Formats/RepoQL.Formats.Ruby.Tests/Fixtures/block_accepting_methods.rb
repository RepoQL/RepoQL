class BlockExamples
  def around
    yield if block_given?
  end

  def explicit(&block)
    block.call
  end
end
