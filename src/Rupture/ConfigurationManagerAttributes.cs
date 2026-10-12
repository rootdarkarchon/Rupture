namespace Rupture;

// The installed settings UI discovers this type name and copies matching public
// fields into its setting properties. Only these two tags are used by Rupture.
internal sealed class ConfigurationManagerAttributes
{
	public int? Order;
	public bool? IsAdvanced;
}
