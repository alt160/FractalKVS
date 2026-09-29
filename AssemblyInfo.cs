using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AbraxasDB")]
[assembly: InternalsVisibleTo("AbxIdentityGenerator")]
#if FRACTALKVS_EXPOSE_HARNESS_INTERNALS
[assembly: InternalsVisibleTo("AbraxasTestHarness")]
#endif
