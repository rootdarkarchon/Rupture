namespace Rupture;

// The installed settings UI discovers this type name and copies matching public
// fields into its setting properties, including the death preset IMGUI drawer.
internal sealed class ConfigurationManagerAttributes
{
	public int? Order;
	public bool? IsAdvanced;
	public System.Action<BepInEx.Configuration.ConfigEntryBase> CustomDrawer;
	public bool HideDefaultButton;
	public string DispName;
}
