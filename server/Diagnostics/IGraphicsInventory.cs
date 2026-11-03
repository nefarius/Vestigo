namespace Vestigo.Diagnostics;

/// <summary>DXGI adapter and output inventory. Does not need an interactive desktop.</summary>
public interface IGraphicsInventory
{
    OperationResult<GraphicsAdapterList> List();
}
