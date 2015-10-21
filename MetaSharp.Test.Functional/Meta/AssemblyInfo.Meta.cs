﻿using MetaSharp;
using System.Collections.Generic;

[assembly: MetaReference(@"..\..\Bin\System.Collections.Immutable.dll")]
[assembly: MetaReference(@"..\..\MetaSharp.Test\Bin\MetaSharp.Test.dll")]

namespace MetaSharp.Test.Meta {
    public static class CompleteFiles {
        public static Either<IEnumerable<MetaError>, Output> CompletePOCOModels(MetaContext context) {
            return context.Complete("POCOViewModels.cs");
        }
    }
}

