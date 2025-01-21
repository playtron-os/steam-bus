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
}