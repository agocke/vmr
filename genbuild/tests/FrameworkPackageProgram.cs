#if PACKAGE_WINS
System.Console.WriteLine(PackageImmutableMarker.Message);
#else
System.Console.WriteLine(System.Collections.Immutable.ImmutableArray.Create("framework selected")[0]);
#endif
