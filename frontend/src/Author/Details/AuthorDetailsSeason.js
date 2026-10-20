import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Icon from 'Components/Icon';
import Link from 'Components/Link/Link';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { icons, kinds, sortDirections } from 'Helpers/Props';
import hasDifferentItemsOrOrder from 'Utilities/Object/hasDifferentItemsOrOrder';
import translate from 'Utilities/String/translate';
import getToggledRange from 'Utilities/Table/getToggledRange';
import BookRowConnector from './BookRowConnector';
import tableStyles from './AuthorDetailsBookTable.css';
import styles from './AuthorDetailsSeason.css';

class AuthorDetailsSeason extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      lastToggledBook: null
    };
  }

  componentDidMount() {
    this.props.setSelectedState(this.getVisibleItems(this.props));
  }

  componentDidUpdate(prevProps) {
    const {
      sortKey,
      sortDirection,
      setSelectedState
    } = this.props;

    const visibleItems = this.getVisibleItems(this.props);

    if (sortKey !== prevProps.sortKey ||
        sortDirection !== prevProps.sortDirection ||
        hasDifferentItemsOrOrder(this.getVisibleItems(prevProps), visibleItems)
    ) {
      setSelectedState(visibleItems);
    }
  }

  //
  // Control

  // Talearr: the rows on screen, main works first and then the open "Other listings" section,
  // so selection and shift-click ranges only reach books the user can see.
  getVisibleItems(props) {
    return props.visibleItems || props.items;
  }

  //
  // Listeners

  onMonitorBookPress = (bookId, monitored, { shiftKey }) => {
    const lastToggled = this.state.lastToggledBook;
    const bookIds = [bookId];

    if (shiftKey && lastToggled) {
      const items = this.getVisibleItems(this.props);
      const { lower, upper } = getToggledRange(items, bookId, lastToggled);

      for (let i = lower; i < upper; i++) {
        bookIds.push(items[i].id);
      }
    }

    this.setState({ lastToggledBook: bookId });

    this.props.onMonitorBookPress(_.uniq(bookIds), monitored);
  };

  onSelectedChange = ({ id, value, shiftKey = false }) => {
    const {
      onSelectedChange
    } = this.props;

    return onSelectedChange(this.getVisibleItems(this.props), id, value, shiftKey);
  };

  //
  // Rows

  renderBookRow(item) {
    const {
      columns,
      isEditorActive,
      selectedState,
      selectedMediaType
    } = this.props;

    return (
      <BookRowConnector
        key={item.id}
        columns={columns}
        {...item}
        selectedMediaType={selectedMediaType}
        onMonitorBookPress={this.onMonitorBookPress}
        isEditorActive={isEditorActive}
        isSelected={selectedState[item.id]}
        onSelectedChange={this.onSelectedChange}
      />
    );
  }

  renderOtherListings(titleColumns) {
    const {
      otherGroups,
      isOtherListingsExpanded,
      sortKey,
      sortDirection,
      onSortPress,
      onTableOptionChange,
      onOtherListingsPress
    } = this.props;

    const otherCount = otherGroups.reduce((count, group) => count + group.items.length, 0);

    if (otherCount === 0) {
      return null;
    }

    const visibleColumnCount = titleColumns.filter((column) => column.isVisible).length || 1;
    const toggleTitle = isOtherListingsExpanded ?
      translate('TalearrOtherListingsHide') :
      translate('TalearrOtherListingsShow');

    return (
      <div className={styles.otherListings}>
        <Link
          className={styles.otherListingsToggle}
          title={toggleTitle}
          onPress={onOtherListingsPress}
        >
          <Icon
            className={styles.otherListingsIcon}
            name={isOtherListingsExpanded ? icons.COLLAPSE : icons.EXPAND}
            size={18}
          />

          <span className={styles.otherListingsLabel}>
            {translate('TalearrOtherListings', [otherCount])}
          </span>

          <span className={styles.otherListingsHelp}>
            {translate('TalearrOtherListingsHelpText')}
          </span>
        </Link>

        {
          isOtherListingsExpanded &&
            <div className={styles.otherListingsBooks}>
              <Table
                className={tableStyles.table}
                columns={titleColumns}
                sortKey={sortKey}
                sortDirection={sortDirection}
                onSortPress={onSortPress}
                onTableOptionChange={onTableOptionChange}
              >
                <TableBody>
                  {
                    otherGroups.map((group) => {
                      return (
                        <React.Fragment key={group.key}>
                          <tr className={styles.otherGroupRow}>
                            <td
                              className={styles.otherGroupLabel}
                              colSpan={visibleColumnCount}
                            >
                              {group.label}

                              <span className={styles.otherGroupCount}>
                                {group.items.length}
                              </span>
                            </td>
                          </tr>

                          {
                            group.items.map((item) => this.renderBookRow(item))
                          }
                        </React.Fragment>
                      );
                    })
                  }
                </TableBody>
              </Table>
            </div>
        }
      </div>
    );
  }

  //
  // Render

  render() {
    const {
      items,
      isEditorActive,
      columns,
      sortKey,
      sortDirection,
      onSortPress,
      onTableOptionChange,
      hasRootFolder,
      selectedMediaType
    } = this.props;

    let titleColumns = columns;
    if (!isEditorActive) {
      titleColumns = columns.filter((x) => x.name !== 'select');
    }

    titleColumns = titleColumns.map((column) => ({
      ...column,
      className: column.className || tableStyles[column.name]
    }));

    if (!hasRootFolder) {
      return (
        <div className={styles.bookType}>
          <Alert kind={kinds.WARNING}>
            {translate('AuthorDetailsSeasonNoMediaTypeRootFolder', { mediaType: selectedMediaType })}
          </Alert>
        </div>
      );
    }

    return (
      <div
        className={styles.bookType}
      >
        <div className={styles.books}>
          <Table
            className={tableStyles.table}
            columns={titleColumns}
            sortKey={sortKey}
            sortDirection={sortDirection}
            onSortPress={onSortPress}
            onTableOptionChange={onTableOptionChange}
          >
            <TableBody>
              {
                items.map((item) => this.renderBookRow(item))
              }
            </TableBody>
          </Table>
        </div>

        {this.renderOtherListings(titleColumns)}
      </div>
    );
  }
}

AuthorDetailsSeason.propTypes = {
  sortKey: PropTypes.string,
  sortDirection: PropTypes.oneOf(sortDirections.all),
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  isEditorActive: PropTypes.bool.isRequired,
  selectedState: PropTypes.object.isRequired,
  columns: PropTypes.arrayOf(PropTypes.object).isRequired,
  onTableOptionChange: PropTypes.func.isRequired,
  onExpandPress: PropTypes.func.isRequired,
  setSelectedState: PropTypes.func.isRequired,
  onSelectedChange: PropTypes.func.isRequired,
  onSortPress: PropTypes.func.isRequired,
  onMonitorBookPress: PropTypes.func.isRequired,
  uiSettings: PropTypes.object.isRequired,
  hasRootFolder: PropTypes.bool.isRequired,
  selectedMediaType: PropTypes.string.isRequired,
  visibleItems: PropTypes.arrayOf(PropTypes.object),
  otherGroups: PropTypes.arrayOf(PropTypes.shape({
    key: PropTypes.string.isRequired,
    label: PropTypes.string.isRequired,
    items: PropTypes.arrayOf(PropTypes.object).isRequired
  })),
  isOtherListingsExpanded: PropTypes.bool,
  onOtherListingsPress: PropTypes.func
};

AuthorDetailsSeason.defaultProps = {
  otherGroups: [],
  isOtherListingsExpanded: false
};

export default AuthorDetailsSeason;
