; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

 Rule ID       | Category                   | Severity | Notes                                                           
---------------|----------------------------|----------|-----------------------------------------------------------------
 NUMOSCHUNK001 | Numos.API.ChunkData        | Error    | Chunk data property must be an unimplemented partial definition 
 NUMOSCHUNK002 | Numos.API.ChunkData        | Error    | Chunk data property must be static                              
 NUMOSCHUNK003 | Numos.API.ChunkData        | Error    | Chunk data property must have type ChunkField<T>                
 NUMOSCHUNK004 | Numos.API.ChunkData        | Error    | Type containing chunk data must be partial                      
 NUMOSCHUNK005 | Numos.API.ChunkData        | Error    | Type containing chunk data must not be generic                  
 NUMOSCHUNK006 | Numos.API.ChunkData        | Error    | Chunk data key is blank                                         
 NUMOSCHUNK007 | Numos.API.ChunkData        | Error    | Saved chunk data needs an explicit key                          
 NUMOSCHUNK008 | Numos.API.ChunkData        | Error    | Saved chunk data needs an unmanaged element type                
 NUMOSCHUNK009 | Numos.API.ChunkData        | Error    | Chunk data length is negative                                   
 NUMOSCHUNK010 | Numos.API.ChunkData        | Error    | Chunk data property must be visible to its namespace            
 NUMOSCHUNK011 | Numos.API.ChunkData        | Error    | Chunk data key registered with conflicting definitions          
 NUMOSCHUNK012 | Numos.API.ChunkData        | Warning  | Chunk data key declared more than once                          
 NUMOSCHUNK013 | Numos.API.ChunkData        | Error    | Chunk data requires C# 14                                       
 NUMOSCHUNK014 | Numos.API.ChunkData        | Warning  | Chunk data accessor name collides with another member           
 NUMOSCHUNK015 | Numos.API.ChunkData        | Error    | Chunk data accessor name is not a valid identifier              
 NUMOSCHUNK016 | Numos.API.ChunkData        | Warning  | Saved chunk field can't be written to a replay file             
 NUMOSCOMMAND001 | Numos.API.SolverCommand  | Error    | Solver command must be declared in a partial class solver
 NUMOSCOMMAND002 | Numos.API.SolverCommand  | Error    | Solver command must be an instance partial void declaration
 NUMOSCOMMAND003 | Numos.API.SolverCommand  | Error    | Solver command parameter is not supported
 NUMOSCOMMAND004 | Numos.API.SolverCommand  | Error    | Solver command has no Apply method
 NUMOSCOMMAND005 | Numos.API.SolverCommand  | Error    | Two solver commands have the same command id
 NUMOSCOMMAND006 | Numos.API.SolverCommand  | Error    | Solver command wire name is blank
 NUMOSCOMPONENT001 | Numos.API.ChunkComponent   | Error    | Chunk component must be a non-generic partial struct
 NUMOSCOMPONENT002 | Numos.API.ChunkComponent   | Error    | Chunk component must be visible to its namespace
 NUMOSCOMPONENT003 | Numos.API.ChunkComponent   | Error    | Chunk component key is blank
 NUMOSCOMPONENT004 | Numos.API.ChunkComponent   | Warning  | Chunk component field is not marked
 NUMOSCOMPONENT005 | Numos.API.ChunkComponent   | Error    | Chunk component member can't be a chunk field
 NUMOSCOMPONENT006 | Numos.API.ChunkComponent   | Error    | Saved chunk component field needs a primitive or enum type
 NUMOSCOMPONENT007 | Numos.API.ChunkComponent   | Error    | Scratch chunk component field needs an unmanaged type
 NUMOSCOMPONENT008 | Numos.API.ChunkComponent   | Error    | Per-chunk length is negative
 NUMOSCOMPONENT009 | Numos.API.ChunkComponent   | Error    | Chunk component accessor name is not a valid identifier
 NUMOSCOMPONENT010 | Numos.API.ChunkComponent   | Warning  | Chunk component accessor collides with another member
 NUMOSCOMPONENT011 | Numos.API.ChunkComponent   | Error    | Two chunk component fields have the same wire name
 NUMOSCOMPONENT012 | Numos.API.ChunkComponent   | Error    | Chunk components require C# 14
 NUMOSCOMPONENT013 | Numos.API.ChunkComponent   | Error    | Chunk component has no fields
 NUMOSCOMPONENT014 | Numos.API.ChunkComponent   | Error    | Chunk component member uses a generated name
 NUMOSCOMPONENT015 | Numos.API.ChunkComponent   | Warning  | Chunk component field initializer is not used
 NUMOSDEP001   | Numos.API.SolverDependency | Error    | Type containing solver dependencies must be partial             
 NUMOSDEP002   | Numos.API.SolverDependency | Error    | Solver dependencies must be declared in a class                 
 NUMOSDEP003   | Numos.API.SolverDependency | Error    | Solver dependency must be an unimplemented partial property     
 NUMOSDEP004   | Numos.API.SolverDependency | Error    | Solver dependency must not be static                            
 NUMOSDEP005   | Numos.API.SolverDependency | Error    | Solver dependency must have a reference type                    
 NUMOSDEP006   | Numos.API.SolverDependency | Warning  | Solver dependency nullability disagrees with Optional           
 NUMOSDEP007   | Numos.API.SolverDependency | Warning  | Type with solver dependencies is not a world solver             
 NUMOSDEP008   | Numos.API.SolverDependency | Error    | Base class injects dependencies without an overridable hook     
 NUMOSDEP009   | Numos.API.SolverDependency | Error    | Type implements dependency injection by hand                    
 NUMOSDEP010   | Numos.API.SolverDependency | Error    | Solver class must not be file-local                             
 NUMOSSOLVER001 | Numos.API.Authoring       | Warning  | Solver lambda writes a captured variable
 NUMOSSOLVER002 | Numos.API.Authoring       | Warning  | Solver reads a topology view that is always empty
 NUMOSSOLVER003 | Numos.API.Authoring       | Warning  | Solver code uses a value that differs on replay
 NUMOSSOLVER004 | Numos.API.Authoring       | Warning  | Solver view is stored in a field or property
 NUMOSSOLVER005 | Numos.API.Authoring       | Warning  | Dangerous chunk view used outside solver code
 NUMOSSOLVER010 | Numos.API.Solver          | Error    | Type containing a solver class must be partial
 NUMOSSOLVER011 | Numos.API.Solver          | Error    | Solver class can't have a generated descriptor
 NUMOSSOLVER012 | Numos.API.Solver          | Error    | Solver class does not implement IAtmosWorldSolver
 NUMOSSOLVER013 | Numos.API.Solver          | Error    | Solver class has no parameterless constructor
 NUMOSSOLVER014 | Numos.API.Solver          | Error    | Solver name is blank
 NUMOSSOLVER015 | Numos.API.Solver          | Error    | Solver sets both After and Before
 NUMOSSOLVER016 | Numos.API.Solver          | Error    | Solver rate is invalid
 NUMOSSOLVER017 | Numos.API.Solver          | Warning  | Solver runs every tick
 NUMOSSOLVER018 | Numos.API.Solver          | Error    | Solver name is declared more than once
 NUMOSSOLVER019 | Numos.API.Solver          | Error    | Solver neighbor selection is invalid
 NUMOSSOLVER020 | Numos.API.Solver          | Error    | Solver class implements its descriptor by hand
 NUMOSSOLVER021 | Numos.API.Solver          | Error    | Neighbor selector is not in a solver class
 NUMOSSTATE001 | Numos.State                | Error    | Serializable type must be partial
 NUMOSSTATE002 | Numos.State                | Error    | Serializable type must not be generic
 NUMOSSTATE005 | Numos.State                | Error    | Two serialized members hash to the same id
 NUMOSSTATE007 | Numos.State                | Error    | Serialized member type is not supported
 NUMOSSTATE008 | Numos.State                | Error    | Serializable type declares a member the generator owns
 NUMOSSTATE009 | Numos.State                | Error    | Solver with serialized members must be a class
 NUMOSSTATE011 | Numos.State                | Error    | Member is marked both serialized and ignored
 NUMOSSTATE012 | Numos.State                | Error    | Serialized member must be an instance member
 NUMOSSTATE013 | Numos.State                | Error    | Serializable class derives from a class the generator can't extend
 NUMOSSTATE014 | Numos.State                | Error    | Serializable type has no constructor the reader can call
 NUMOSSTATE015 | Numos.State                | Error    | Key is blank
 NUMOSSTATE016 | Numos.State                | Warning  | Mutable member is neither serialized nor ignored
 NUMOSSTATE017 | Numos.State                | Error    | Wire name is blank
 NUMOSSTATE018 | Numos.State                | Error    | Solver configuration key is not a constant
 NUMOSSTATE019 | Numos.State                | Warning  | Solver configuration can't be registered automatically
 NUMOSSTATE020 | Numos.State                | Error    | Solver state member can't be restored in place
 NUMOSSTATE021 | Numos.State                | Error    | Snapshot type must be partial
 NUMOSSTATE022 | Numos.State                | Error    | Snapshot type has the wrong shape
 NUMOSSTATE023 | Numos.State                | Error    | Live type can't have a generated snapshot
 NUMOSSTATE024 | Numos.State                | Error    | Live type has no serialized members
 NUMOSSTATE025 | Numos.State                | Error    | Live member is not visible to its snapshot
 NUMOSSTATE026 | Numos.State                | Error    | Live member can't be copied into a snapshot
 NUMOSSTATE027 | Numos.State                | Error    | Snapshot declares a member the generator creates
 NUMOSSTATE028 | Numos.State                | Error    | Count member is not a settable int
 NUMOSSTATE029 | Numos.State                | Error    | Count needs an array
 NUMOSSTATE030 | Numos.State                | Error    | Live type has more than one snapshot
 NUMOSSTATE031 | Numos.State                | Error    | Count only applies to live types
 NUMOSSTATE032 | Numos.State                | Error    | Serializable type must not be file-local
 NUMOSSTATE033 | Numos.State                | Error    | [NumosSerialize] is on a property's backing field
 NUMOSSTATE034 | Numos.State                | Error    | Serialized member must not be an indexer
 NUMOSSTATE035 | Numos.State                | Error    | Member is marked scratch and serialized or ignored
 NUMOSSTATE036 | Numos.State                | Error    | [NumosScratch] is outside a class solver
 NUMOSSTATE037 | Numos.State                | Error    | Scratch member can't be reset
 NUMOSSTATE038 | Numos.State                | Error    | Scratch initializer can't be repeated
 NUMOSSTATE039 | Numos.State                | Error    | Simulation component has the wrong shape
 NUMOSSTATE040 | Numos.State                | Error    | Simulation component key is blank
 NUMOSSTATE041 | Numos.State                | Warning  | HostEditable has no effect here
