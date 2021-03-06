using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Appson.Composer.Cache;
using Appson.Composer.CompositionalQueries;


namespace Appson.Composer.Factories
{
	public class LocalComponentFactory : ILocalComponentFactory
	{
		private IComposer _composer;

		private readonly Type _targetType;
		private IComponentCache _componentCache;

		private ConstructorInfo _targetConstructor;
		private List<ConstructorArgSpecification> _constructorArgs;
		private readonly List<InitializationPointSpecification> _initializationPoints;
		private List<Action<IComposer, object>> _compositionNotificationMethods;
	    private ICompositionalQuery _componentCacheQuery;

		#region Constructors

		public LocalComponentFactory(Type targetType)
		{
            _targetType = targetType ?? throw new ArgumentNullException(nameof(targetType));

			_composer = null;
			_componentCache = null;
            _componentCacheQuery = null;
			_targetConstructor = null;
			_constructorArgs = null;
			_initializationPoints = new List<InitializationPointSpecification>();
			_compositionNotificationMethods = null;
		}

		#endregion

		#region IComponentFactory Members

		public void Initialize(IComposer composer)
		{
			if (_composer != null)
				return;

			if (_targetType == null)
				throw new InvalidOperationException("TargetType is not specified.");

			if (!composer.Configuration.DisableAttributeChecking && !ComponentContextUtils.HasComponentAttribute(_targetType))
				throw new CompositionException("The type '" + _targetType +
				                               "' is not a component, but it is being registered as one. Only classes marked with [Component] attribute can be registered.");

			_composer = composer;
			CompleteConfiguration();
		}

		public IEnumerable<Type> GetContractTypes()
		{
			return ComponentContextUtils.FindContracts(_targetType);
		}

		public object GetComponentInstance(ContractIdentity contract, IEnumerable<ICompositionListener> listenerChain)
		{
			// Check if the factory is initialized

			if (_composer == null)
				throw new InvalidOperationException(
					"LocalComponentFactory should be initialized before calling GetComponentInstance method.");

			if (_componentCache == null)
			{
				// If the component is not cached at all, then unique instances should
				// be created for each call, then locking does not help and just
				// degrades performance. So, create without locking.

				var newComponent = CreateComponent(contract, listenerChain);
				return NotifyRetrieved(newComponent.ComponentInstance,
				                       newComponent.OriginalComponentInstance,
				                       contract, listenerChain);
			}

			// Check if the component is cached, and ready to deliver

			var componentCacheEntry = _componentCache.GetFromCache(contract);
			if (componentCacheEntry != null)
			{
				return NotifyRetrieved(componentCacheEntry.ComponentInstance,
				                       componentCacheEntry.OriginalComponentInstance,
				                       contract, listenerChain);
			}

			// If the component is cached, then lock the component instance
			// to avoid creation of more than one cached components per config 
			// when called in concurrent manner

			lock (_componentCache.SynchronizationObject)
			{
				// Double-check the initialization to avoid rendezvouz

				componentCacheEntry = _componentCache.GetFromCache(contract);
				if (componentCacheEntry != null)
				{
					return NotifyRetrieved(componentCacheEntry.ComponentInstance,
					                       componentCacheEntry.OriginalComponentInstance,
					                       contract, listenerChain);
				}

				componentCacheEntry = CreateComponent(contract, listenerChain);
				return NotifyRetrieved(componentCacheEntry.ComponentInstance,
				                       componentCacheEntry.OriginalComponentInstance,
				                       contract, listenerChain);
			}
		}

		#endregion

		#region Override methods

		public override string ToString()
		{
			return _targetType != null ? _targetType.AssemblyQualifiedName : base.ToString();
		}

		#endregion

		#region Public properties

		public Type TargetType => _targetType;

	    public ConstructorInfo TargetConstructor
		{
			get => _targetConstructor;
	        set
			{
				if (_composer != null)
					throw new InvalidOperationException("Cannot change TargetConstructor when the factory is initialized.");

				_targetConstructor = value;
			}
		}

		public List<ConstructorArgSpecification> ConstructorArgs
		{
			get
			{
			    if (_composer != null)
					throw new InvalidOperationException("Cannot access ConstructorArgs when the factory is initialized.");

			    return _constructorArgs ?? (_constructorArgs = new List<ConstructorArgSpecification>());
			}
		}

		public List<InitializationPointSpecification> InitializationPoints
		{
			get
			{
				if (_composer != null)
					throw new InvalidOperationException("Cannot access InitializationPoints when the factory is initialized.");

				return _initializationPoints;
			}
		}

	    public List<Action<IComposer, object>> CompositionNotificationMethods
	    {
	        get
	        {
	            if (_composer != null)
	                throw new InvalidOperationException("Cannot access CompositionNotificationMethods when the factory is initialized.");

	            return _compositionNotificationMethods ?? (_compositionNotificationMethods = new List<Action<IComposer, object>>());
	        }
        }

	    public ICompositionalQuery ComponentCacheQuery
	    {
	        get
	        {
	            if (_composer != null)
	                throw new InvalidOperationException("Cannot access ComponentCacheQuery when the factory is initialized.");

	            return _componentCacheQuery;
	        }
	        set
	        {
	            if (_composer != null)
	                throw new InvalidOperationException("Cannot access ComponentCacheQuery when the factory is initialized.");

	            _componentCacheQuery = value;
	        }
	    }

	    #endregion

		#region Private helper methods

		private void CompleteConfiguration()
		{
		    try
		    {
		        LoadInitializationPoints();
		        LoadTargetConstructor();
		        LoadComponentCacheQuery();
		        LoadComponentCache();
		        LoadCompositionNotificationMethods();
		    }
		    catch(Exception e)
		    {
		        throw new CompositionException(
                    $"Could not initialize LocalComponentFactory for type '{_targetType.FullName}'", 
                    e);
		    }
		}

		private void LoadTargetConstructor()
		{
			// If the constructor arguments are specified by the creator of the factory,
			// ignore finding the constructor. The constructor to be used will be bound
			// when creating the component.

			if (_constructorArgs != null)
				return;

			// Ignore finding the constructor if the creator of the factory has specified one.
			// Just extract the constructor args.

			if (_targetConstructor == null)
			{
				// Find the appropriate constructor for instantiating the component.
				// Order of precedence:
				//     1. The one marked with [CompositionConstructor]
				//     2. If there's a single public constructor, use it.
				//     3. If there's the default constructor, use it.
				// And it is an exception if none of the above is found.

				var candidateConstructors = _targetType.GetConstructors();
				_targetConstructor = FindMarkedConstructor(_targetType, candidateConstructors) ??
				                     FindSingleConstructor(candidateConstructors) ??
				                     FindDefaultConstructor(candidateConstructors);

				if (_targetConstructor == null)
					throw new CompositionException(
						"There's no appropriate constructor identified as the composition constructor for type '" + _targetType.FullName +
						"'" +
						"You can fix this by using [CompositionConstructor] attribute on the constructor that you intend to be used by Composer.");
			}

			_constructorArgs = new List<ConstructorArgSpecification>();
			string[] queryNames = null;

			if (ComponentContextUtils.HasCompositionConstructorAttribute(_targetConstructor))
				queryNames = ComponentContextUtils.GetCompositionConstructorAttribute(_targetConstructor).Names;

			foreach (var parameterInfo in _targetConstructor.GetParameters())
			{
				if (!_composer.Configuration.DisableAttributeChecking && !ComponentContextUtils.HasContractAttribute(parameterInfo.ParameterType))
					throw new CompositionException(
					        $"Parameter '{parameterInfo.Name}' of the constructor of type '{_targetType.FullName}' is not of a Contract type. " +
					        "All parameters of the composition constructor must be of Contract types, so that Composer can query for a component and pass it to them.");

				if ((queryNames != null) && (queryNames.Length > parameterInfo.Position))
					_constructorArgs.Add(new ConstructorArgSpecification(true,
					                                                     new ComponentQuery(parameterInfo.ParameterType,
					                                                                        queryNames[parameterInfo.Position])));
				else
					_constructorArgs.Add(new ConstructorArgSpecification(true, new ComponentQuery(parameterInfo.ParameterType, null)));
			}

			if ((queryNames != null) && (queryNames.Length > _constructorArgs.Count))
				throw new CompositionException("Extra names are specified for the constructor of type '" +
				                               _targetType.FullName + "'");
		}

		private void LoadInitializationPoints()
		{
			// Check two categories of members for being an initialization point:
			//   1. Public fields
			//   2. Public properties
			// Check and add them to the list of initialization points if they
			// are not already registered.

			foreach (var fieldInfo in _targetType.GetFields())
			{
				ComponentContextUtils.CheckAndAddInitializationPoint(_composer, _initializationPoints, fieldInfo);
			}

			foreach (var fieldInfo in _targetType.GetProperties())
			{
				ComponentContextUtils.CheckAndAddInitializationPoint(_composer, _initializationPoints, fieldInfo);
			}
		}

	    private void LoadComponentCacheQuery()
	    {
	        if (_componentCacheQuery != null)
                return;

            var attribute = ComponentContextUtils.GetComponentCacheAttribute(_targetType);
	        if (attribute == null)
	        {
	            _componentCacheQuery = new ComponentQuery(typeof(DefaultComponentCache), null);
                return;
	        }

	        if (attribute.ComponentCacheType == null)
	        {
	            _componentCacheQuery = null;
                return;
	        }

            _componentCacheQuery = new ComponentQuery(attribute.ComponentCacheType, attribute.ComponentCacheName);
        }

        private void LoadComponentCache()
		{
			if (_componentCacheQuery == null || _componentCacheQuery is NullQuery)
			{
				_componentCache = null;
				return;
			}

			var result = _componentCacheQuery.Query(_composer);
			if (result == null)
				throw new CompositionException($"Can not register component type {_targetType.FullName} because " +
				                               $"the specified ComponentCache contract ({_componentCache}) could not be queried from Composer.");

			if (!(result is IComponentCache))
				throw new CompositionException($"Component cache type {result.GetType().FullName} that is specified " +
				                               $"as component cache handler on component {_targetType.FullName} does not implement " +
				                               "IComponentCache interface.");

			_componentCache = (IComponentCache) result;
		}

		private void LoadCompositionNotificationMethods()
		{
		    var methodsFound = ComponentContextUtils.FindCompositionNotificationMethods(_targetType).ToList();
		    _compositionNotificationMethods = _compositionNotificationMethods?.Concat(methodsFound).ToList() ?? methodsFound;
		}

		private void InvokeCompositionNotifications(object componentInstance)
		{
			if (_compositionNotificationMethods == null)
				return;

			foreach (var method in _compositionNotificationMethods)
			{
			    method(_composer, componentInstance);
			}
		}

		private static ConstructorInfo FindMarkedConstructor(Type targetType,
		                                                     IEnumerable<ConstructorInfo> candidateConstructors)
		{
			var markedConstructors =
				candidateConstructors.Where(ComponentContextUtils.HasCompositionConstructorAttribute).ToArray();

			if (markedConstructors.Length == 0)
				return null;

			if (markedConstructors.Length > 1)
				throw new CompositionException("The type '" + targetType.FullName +
				                               "' has more than one constructor marked with [CompositionConstructor] attribute.");

			return markedConstructors[0];
		}

		private static ConstructorInfo FindSingleConstructor(IEnumerable<ConstructorInfo> candidateConstructors)
		{
			return candidateConstructors.Count() == 1 ? candidateConstructors.First() : null;
		}

		private static ConstructorInfo FindDefaultConstructor(IEnumerable<ConstructorInfo> candidateConstructors)
		{
			return candidateConstructors.FirstOrDefault(c => c.GetParameters().Length == 0);
		}

		private ComponentCacheEntry CreateComponent(ContractIdentity contract, IEnumerable<ICompositionListener> listenerChain)
		{
			// Prepare the constructor information for instantiating the object.

			var constructorArguments = PrepareConstructorArguments();
			var targetConstructor = FindTargetConstructor(constructorArguments);

			// Save the original component instance reference, so that
			// we can apply initialization points to it later, as the
			// composition listeners may change the reference to a
			// wrapped one.

			object originalComponentInstance = targetConstructor.Invoke(constructorArguments.ToArray());

			// After constructing the component object, first process
			// all composition listeners so that if the reference should
			// change, it changes before setting it to the cache.
			// Otherwise, in circular dependency scenarios, dependent
			// components may get unwrapped component where the component
			// is wrapped by composition listeners.

			object componentInstance = NotifyCreated(originalComponentInstance, contract, listenerChain);

			// Store the cache, so that if there is a circular dependency,
			// applying initialization points is not blocked and chained
			// recursively.

			var result = new ComponentCacheEntry
			             	{
			             		ComponentInstance = componentInstance,
			             		OriginalComponentInstance = originalComponentInstance
			             	};

		    _componentCache?.PutInCache(contract, result);

		    // Complete the object initialization by applying the initialization
			// points. They should be applied to the original component instance,
			// as the reference may have been changed by composition listeners to
			// an instance that does not have the original configuration points.

			var initializationPointResults = ApplyInitializationPoints(originalComponentInstance);

			// Inform all composition listeners of the newly composed
			// component instance by calling OnComponentComposed method.

			NotifyComposed(componentInstance, originalComponentInstance, initializationPointResults, contract, listenerChain);

			// The composition is now finished for the component instance.
			// See if an [OnCompositionComplete] method is specified, call it.
			// This should be called on the original component instance
			// for the same reason stated above.

			InvokeCompositionNotifications(componentInstance);
			return result;
		}

		private List<object> PrepareConstructorArguments()
		{
			var constructorArguments = new List<object>();

            if (_constructorArgs == null)
                throw new CompositionException("LocalComponentFactory is not property initialized. _constructorArgs is null.");

			foreach (var cas in _constructorArgs)
			{
				if (cas.Query == null)
					throw new CompositionException("Query is null for a constructor argument, for the type '" +
					                               _targetType.FullName + "'");

				object argumentValue = cas.Query.Query(_composer);

				if ((argumentValue == null) && (cas.Required))
					throw new CompositionException("Required constructor argument can not be queried for type '" +
					                               _targetType.FullName + "'");

				constructorArguments.Add(argumentValue);
			}
			return constructorArguments;
		}

		private object NotifyCreated(object originalComponentInstance, ContractIdentity contract,
		                             IEnumerable<ICompositionListener> listenerChain)
		{
			var componentInstance = originalComponentInstance;

			foreach (var compositionListener in listenerChain)
			{
				compositionListener.OnComponentCreated(contract, this, _targetType, ref componentInstance, originalComponentInstance);
			}

			return componentInstance;
		}

		private void NotifyComposed(object componentInstance, object originalComponentInstance,
		                            List<object> initializationPointResults, ContractIdentity contract,
		                            IEnumerable<ICompositionListener> listenerChain)
		{
			foreach (var compositionListener in listenerChain)
			{
				compositionListener.OnComponentComposed(contract, _initializationPoints, initializationPointResults, _targetType,
				                                        componentInstance, originalComponentInstance);
			}
		}

		private object NotifyRetrieved(object componentInstance, object originalComponentInstance, ContractIdentity contract,
		                               IEnumerable<ICompositionListener> listenerChain)
		{
			// The component is ready to be delivered.
			// Inform composition listeners about the retrieval.

			var result = componentInstance;

			foreach (var compositionListener in listenerChain)
			{
				compositionListener.OnComponentRetrieved(contract, this, _targetType, ref result, originalComponentInstance);
			}

			return result;
		}

		private ConstructorInfo FindTargetConstructor(List<object> constructorArguments)
		{
			ConstructorInfo targetConstructor = _targetConstructor;

			if (targetConstructor == null)
			{
                if (constructorArguments.Any(arg => arg == null))
                    throw new CompositionException($"Canntot find an appropriate constructor to initialize type {_targetType.FullName} " +
                                                   "because some of the constructor arguments are null. You can specify the constructor " +
                                                   "to use to avoid this problem when passing null values.");

				var constructorArgTypes = constructorArguments.Select(arg => arg.GetType()).ToArray();
				targetConstructor = _targetType.GetConstructor(constructorArgTypes.ToArray());
			}

			if (targetConstructor == null)
				throw new CompositionException($"No constructor found for the component type '{_targetType.FullName}'");

			return targetConstructor;
		}

		private List<object> ApplyInitializationPoints(object originalComponentInstance)
		{
			var initializationPointResults = new List<object>();

			foreach (var initializationPoint in _initializationPoints)
			{
				if (initializationPoint.Query == null)
					throw new CompositionException(
					        $"Query is null for initialization point '{initializationPoint.Name}' on component instance of type '{_targetType.FullName}'");

				var initializationPointResult = initializationPoint.Query.Query(_composer);

				// Check if the required initialization points get a value.
				if (initializationPoint.Required && initializationPointResult == null)
					throw new CompositionException(
					        $"Could not fill initialization point '{initializationPoint.Name}' of type '{_targetType.FullName}'.");

				initializationPointResults.Add(initializationPointResult);
				ComponentContextUtils.ApplyInitializationPoint(originalComponentInstance,
				                                               initializationPoint.Name,
				                                               initializationPoint.MemberType,
				                                               initializationPointResult);
			}

			return initializationPointResults;
		}


		#endregion
	}
}