module Searchable
  include Enumerable
  VERSION = "1.0.0"

  def search(query)
    query
  end

  def build_index(items = [])
    items
  end
end
