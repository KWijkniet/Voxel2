using System.Collections.Generic;

public class ObjectPool<T> where T : class, new()
{
    private readonly Stack<T> pool = new Stack<T>();

    public T Get() => pool.Count > 0 ? pool.Pop() : new T();
    public void Return(T obj) => pool.Push(obj);
}