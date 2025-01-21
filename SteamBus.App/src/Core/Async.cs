public static class AsyncUtils
{
    public static async Task<bool> WaitForConditionAsync(Func<bool> condition, TimeSpan delay, TimeSpan timeout)
    {
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(delay);
        }

        return false;
    }

    public static async Task<bool> WaitForConditionAsync(Func<Task<bool>> asyncCondition, TimeSpan delay, TimeSpan timeout)
    {
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < timeout)
        {
            if (await asyncCondition())
            {
                return true;
            }

            await Task.Delay(delay);
        }

        return false;
    }
}