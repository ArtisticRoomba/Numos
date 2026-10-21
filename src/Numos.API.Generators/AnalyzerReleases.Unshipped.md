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
 NUMOSCHUNK007 | Numos.API.ChunkData        | Error    | Rollback chunk data needs an explicit key                       
 NUMOSCHUNK008 | Numos.API.ChunkData        | Error    | Rollback chunk data needs an unmanaged element type             
 NUMOSCHUNK009 | Numos.API.ChunkData        | Error    | Chunk data length is negative                                   
 NUMOSCHUNK010 | Numos.API.ChunkData        | Error    | Chunk data property must be visible to its namespace            
 NUMOSCHUNK011 | Numos.API.ChunkData        | Error    | Chunk data key registered with conflicting definitions          
 NUMOSCHUNK012 | Numos.API.ChunkData        | Warning  | Chunk data key declared more than once                          
 NUMOSCHUNK013 | Numos.API.ChunkData        | Error    | Chunk data requires C# 14                                       
 NUMOSCHUNK014 | Numos.API.ChunkData        | Warning  | Chunk data accessor name collides with another member           
 NUMOSCHUNK015 | Numos.API.ChunkData        | Error    | Chunk data accessor name is not a valid identifier              
 NUMOSDEP001   | Numos.API.SolverDependency | Error    | Type containing solver dependencies must be partial             
 NUMOSDEP002   | Numos.API.SolverDependency | Error    | Solver dependencies must be declared in a class                 
 NUMOSDEP003   | Numos.API.SolverDependency | Error    | Solver dependency must be an unimplemented partial property     
 NUMOSDEP004   | Numos.API.SolverDependency | Error    | Solver dependency must not be static                            
 NUMOSDEP005   | Numos.API.SolverDependency | Error    | Solver dependency must have a reference type                    
 NUMOSDEP006   | Numos.API.SolverDependency | Warning  | Solver dependency nullability disagrees with Optional           
 NUMOSDEP007   | Numos.API.SolverDependency | Warning  | Type with solver dependencies is not a world solver             
 NUMOSDEP008   | Numos.API.SolverDependency | Error    | Base class injects dependencies without an overridable hook     
 NUMOSDEP009   | Numos.API.SolverDependency | Error    | Type implements dependency injection by hand                    
 NUMOSSTATE001 | Numos.State                | Error    | Serializable type must be partial
 NUMOSSTATE002 | Numos.State                | Error    | Serializable type must not be generic
 NUMOSSTATE005 | Numos.State                | Error    | Two serialized members hash to the same id
 NUMOSSTATE007 | Numos.State                | Error    | Serialized member type is not supported
 NUMOSSTATE008 | Numos.State                | Error    | Serializable type declares a member the generator owns
 NUMOSSTATE009 | Numos.State                | Error    | Solver objects cannot hold serialized members
 NUMOSSTATE011 | Numos.State                | Error    | Member is marked both serialized and ignored
 NUMOSSTATE012 | Numos.State                | Error    | Serialized member must be an instance member
 NUMOSSTATE013 | Numos.State                | Error    | Serializable class must not derive from another class
 NUMOSSTATE014 | Numos.State                | Error    | Serializable type has no constructor the reader can call
 NUMOSSTATE015 | Numos.State                | Error    | Key is blank
 NUMOSSTATE016 | Numos.State                | Warning  | Mutable member is neither serialized nor ignored
 NUMOSSTATE017 | Numos.State                | Error    | Wire name is blank
 NUMOSSTATE018 | Numos.State                | Error    | Solver configuration key is not a constant
 NUMOSSTATE019 | Numos.State                | Warning  | Solver configuration can't be registered automatically
