import { filterBuilderTypes, filterBuilderValueTypes, filterTypes } from 'Helpers/Props';
import sortByName from 'Utilities/Array/sortByName';

// TaleArr: custom filters on the Wanted pages. The pages are paged on the server, so the
// selected custom filter is sent as one talearrFilter query value (JSON) that the wanted
// endpoints evaluate; monitored is sent the way the built in filters send it.

export const customFilterTypes = {
  missing: 'wantedMissing',
  cutoffUnmet: 'wantedCutoffUnmet'
};

export const filterBuilderProps = [
  {
    name: 'monitored',
    label: () => 'Monitored',
    type: filterBuilderTypes.EXACT,
    valueType: filterBuilderValueTypes.BOOL
  },
  {
    name: 'tags',
    label: () => 'Book Tags',
    type: filterBuilderTypes.ARRAY,
    valueType: filterBuilderValueTypes.TAG
  },
  {
    name: 'authorTags',
    label: () => 'Author Tags',
    type: filterBuilderTypes.ARRAY,
    valueType: filterBuilderValueTypes.TAG
  },
  {
    name: 'genres',
    label: () => 'Genres',
    type: filterBuilderTypes.ARRAY,
    optionsSelector: function(items) {
      const seen = new Set();
      const genres = [];

      items.forEach((item) => {
        (item.genres || []).forEach((genre) => {
          if (!seen.has(genre)) {
            seen.add(genre);
            genres.push({ id: genre, name: genre });
          }
        });
      });

      return genres.sort(sortByName);
    }
  },
  {
    name: 'narrator',
    label: () => 'Narrator',
    type: filterBuilderTypes.STRING
  }
];

// Adds the selected custom filter (if any) to the wanted request.
export function addTalearrCustomFilter(state, sectionState, customFilterType, data) {
  const selectedFilterKey = sectionState && sectionState.selectedFilterKey;
  const customFilter = ((state.customFilters && state.customFilters.items) || [])
    .find((f) => f.type === customFilterType && f.id === selectedFilterKey);

  if (!customFilter) {
    return;
  }

  const rules = [];

  (customFilter.filters || []).forEach((filter) => {
    if (filter.key === 'monitored') {
      data.monitored = filter.type === filterTypes.NOT_EQUAL ? !filter.value : filter.value;
    } else {
      rules.push({ key: filter.key, value: filter.value, type: filter.type });
    }
  });

  if (rules.length) {
    data.talearrFilter = JSON.stringify(rules);
  }
}
