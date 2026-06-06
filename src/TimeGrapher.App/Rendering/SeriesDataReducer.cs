using TimeGrapher.Core.Shared;

namespace TimeGrapher.App.Rendering;

internal static class SeriesDataReducer
{
    public static bool TryReplaceSeriesData(
        GraphSeriesFrame? series,
        List<double> targetX,
        List<double> targetY,
        int targetPointBudget)
    {
        if (series == null)
        {
            return false;
        }

        if (!series.Replace)
        {
            throw new InvalidOperationException($"Graph series '{series.Id}' must be a replace snapshot.");
        }

        ReplaceSeriesData(targetX, targetY, series.X, series.Y, targetPointBudget);
        return true;
    }

    public static void ReplaceSeriesData(
        List<double> targetX,
        List<double> targetY,
        IReadOnlyList<double> sourceX,
        IReadOnlyList<double> sourceY,
        int targetPointBudget)
    {
        targetX.Clear();
        targetY.Clear();

        int count = Math.Min(sourceX.Count, sourceY.Count);
        if (count == 0)
        {
            return;
        }

        int stride = targetPointBudget > 0 && count > targetPointBudget
            ? (int)Math.Ceiling(count / (double)targetPointBudget)
            : 1;

        for (int i = 0; i < count; i += stride)
        {
            targetX.Add(sourceX[i]);
            targetY.Add(sourceY[i]);
        }
    }
}
